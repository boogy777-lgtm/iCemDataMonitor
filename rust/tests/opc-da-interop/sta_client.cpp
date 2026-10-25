// Independent CI-only COM client. All OPC declarations/IIDs come from pinned MIDL.
// Native boundary and registry/lifetime constraints: ADR0019, R2 addendum.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <ocidl.h>
#include <wrl/client.h>
#include "opcda.h"
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <functional>
#include <stdexcept>
#include <string>
#include <vector>
using Microsoft::WRL::ComPtr;
static void require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
static void checked(HRESULT hr, const char* message) {
    if (FAILED(hr)) { std::fprintf(stderr, "%s: HRESULT 0x%08lx\n", message, static_cast<unsigned long>(hr)); throw std::runtime_error(message); }
}
static uint64_t ticks(FILETIME ft) { return (uint64_t(ft.dwHighDateTime) << 32) | ft.dwLowDateTime; }
static void pump_until(const std::function<bool()>& done, DWORD timeout = 3000) {
    HANDLE timer = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    require(timer != nullptr, "CreateEvent");
    const ULONGLONG end = GetTickCount64() + timeout;
    try {
        while (!done() && GetTickCount64() < end) {
            DWORD index = 0;
            HRESULT hr = CoWaitForMultipleHandles(COWAIT_DISPATCH_CALLS | COWAIT_DISPATCH_WINDOW_MESSAGES, 20, 1, &timer, &index);
            require(SUCCEEDED(hr) || hr == RPC_S_CALLPENDING, "COM pump failed");
        }
        require(done(), "STA callback deadline");
    } catch (...) { CloseHandle(timer); throw; }
    CloseHandle(timer);
}
struct Apartment {
    Apartment() { checked(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED), "STA init"); }
    ~Apartment() { CoUninitialize(); }
};
static ComPtr<IOPCServer> connect() {
    CLSID id{};
    checked(CLSIDFromString(L"{C69B6B01-878C-4927-8CC0-C6BA26C4B71A}", &id), "class ID");
    ComPtr<IOPCServer> server;
    checked(CoCreateInstance(id, nullptr, CLSCTX_LOCAL_SERVER, IID_IOPCServer,
                            reinterpret_cast<void**>(server.GetAddressOf())), "external CoCreateInstance");
    return server;
}
struct Group {
    ComPtr<IOPCServer> server;
    ComPtr<IOPCItemMgt> items;
    ComPtr<IOPCGroupStateMgt> state;
    ComPtr<IOPCSyncIO> sync;
    ComPtr<IOPCAsyncIO2> async;
    ComPtr<IConnectionPoint> point;
    OPCHANDLE handle = 0;
    bool removed = false;
    Group(ComPtr<IOPCServer> s, bool active, DWORD rate, OPCHANDLE client) : server(s) {
        DWORD revised = 0;
        checked(server->AddGroup(L"external-STA", active, rate, client, nullptr, nullptr, 0,
                                 &handle, &revised, IID_IOPCItemMgt,
                                 reinterpret_cast<IUnknown**>(items.GetAddressOf())), "AddGroup RPC");
        require(revised >= 10, "rate revision");
        checked(items.As(&state), "group state QI");
        checked(items.As(&sync), "sync QI");
        checked(items.As(&async), "async QI");
        ComPtr<IConnectionPointContainer> container;
        checked(items.As(&container), "CPC QI");
        checked(container->FindConnectionPoint(IID_IOPCDataCallback, &point), "FindConnectionPoint RPC");
    }
    void remove() { checked(server->RemoveGroup(handle, FALSE), "RemoveGroup RPC"); removed = true; }
    ~Group() { if (!removed) server->RemoveGroup(handle, TRUE); }
    std::vector<OPCHANDLE> add(DWORD count) {
        std::vector<OPCITEMDEF> definitions(count);
        wchar_t empty[] = L""; wchar_t a[] = L"var0"; wchar_t b[] = L"var4"; wchar_t c[] = L"var10";
        wchar_t* names[] = {a,b,c};
        for (DWORD i=0; i<count; ++i) {
            definitions[i].szAccessPath = empty; definitions[i].szItemID = names[i%3];
            definitions[i].bActive = TRUE; definitions[i].hClient = i+100;
            definitions[i].vtRequestedDataType = VT_EMPTY;
        }
        OPCITEMRESULT* results = nullptr; HRESULT* errors = nullptr;
        checked(items->AddItems(count, definitions.data(), &results, &errors), "AddItems RPC");
        std::vector<OPCHANDLE> handles;
        bool valid = results && errors;
        if (valid) for (DWORD i=0; i<count; ++i) {
            valid = valid && SUCCEEDED(errors[i]) && results[i].vtCanonicalDataType == (i%3==0 ? VT_UI2 : i%3==1 ? VT_I4 : VT_R4);
            handles.push_back(results[i].hServer);
            CoTaskMemFree(results[i].pBlob);
        }
        CoTaskMemFree(results); CoTaskMemFree(errors);
        require(valid, "AddItems types/per-item results"); return handles;
    }
    void set(bool active, DWORD rate, OPCHANDLE client) {
        DWORD revised = 0; BOOL enabled = active;
        checked(state->SetState(&rate, &revised, &enabled, nullptr, nullptr, nullptr, &client), "SetState RPC");
        require(revised == rate, "SetState revised rate");
        DWORD gotRate = 0, locale = 0; BOOL gotActive = FALSE; LPWSTR name = nullptr;
        LONG bias = 0; FLOAT deadband = 0; OPCHANDLE gotClient = 0, gotServer = 0;
        checked(state->GetState(&gotRate, &gotActive, &name, &bias, &deadband, &locale, &gotClient, &gotServer), "GetState RPC");
        CoTaskMemFree(name);
        require(gotRate == rate && gotActive == enabled && gotClient == client && gotServer == handle, "state coherence");
    }
};
// Apartment-bound: deliberately exposes only IUnknown and generated IOPCDataCallback.
// No IMarshal, FTM, agility, handwritten ABI or custom proxy implementation.
class Sink final : public IOPCDataCallback {
    std::atomic<ULONG> refs{1};
public:
    const DWORD owner = GetCurrentThreadId();
    const uint64_t writer;
    DWORD expectedCount;
    OPCHANDLE expectedGroup = 41;
    bool inactive = false, changedHandle = false, failed = false;
    unsigned calls = 0, reads = 0, cancels = 0;
    DWORD lastTransaction = 0;
    std::function<void(DWORD)> reentrant;
    explicit Sink(uint64_t w, DWORD count) : writer(w), expectedCount(count) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (iid == IID_IUnknown || iid == IID_IOPCDataCallback) { *out = static_cast<IOPCDataCallback*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    void sample(DWORD i, OPCHANDLE client, VARIANT& value, WORD quality, FILETIME time, HRESULT error) {
        require(SUCCEEDED(error), "callback item HRESULT");
        require(client == (i==0 && changedHandle ? 9000 : i+100), "callback client handle");
        if (i%3==0) require(value.vt == VT_UI2 && value.uiVal == 7 && ticks(time)==0 && quality==(inactive ? 0x1c : 0x40), "U16/quality/unknown clock");
        if (i%3==1) require(value.vt == VT_I4 && value.lVal == 1700000000 && ticks(time)==writer && quality==(inactive ? 0x1c : 0xc0), "I32/writer FILETIME/GOOD");
        if (i%3==2) { uint32_t bits=0; std::memcpy(&bits, &value.fltVal, sizeof(bits)); require(value.vt==VT_R4 && bits==0x7fc01234 && ticks(time)==writer && quality==(inactive ? 0x1c : 0xc0), "NaN payload/writer FILETIME/GOOD"); }
    }
    HRESULT event(bool read, DWORD transaction, OPCHANDLE group, DWORD count, OPCHANDLE* clients,
                  VARIANT* values, WORD* qualities, FILETIME* times, HRESULT* errors) noexcept {
        try {
            require(GetCurrentThreadId()==owner, "callback marshalled to owner STA thread");
            require(group==expectedGroup && count==expectedCount, "callback group/count");
            for (DWORD i=0; i<count; ++i) sample(i,clients[i],values[i],qualities[i],times[i],errors[i]);
            ++calls; if (read) ++reads; lastTransaction=transaction;
            if (reentrant) reentrant(transaction);
            return S_OK;
        } catch (const std::exception& e) { std::fprintf(stderr,"STA sink: %s\n",e.what()); failed=true; return E_FAIL; }
        catch (...) { failed=true; return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE OnDataChange(DWORD t, OPCHANDLE g, HRESULT, HRESULT, DWORD n, OPCHANDLE* c, VARIANT* v, WORD* q, FILETIME* f, HRESULT* e) override { return event(false,t,g,n,c,v,q,f,e); }
    HRESULT STDMETHODCALLTYPE OnReadComplete(DWORD t, OPCHANDLE g, HRESULT, HRESULT, DWORD n, OPCHANDLE* c, VARIANT* v, WORD* q, FILETIME* f, HRESULT* e) override { return event(true,t,g,n,c,v,q,f,e); }
    HRESULT STDMETHODCALLTYPE OnWriteComplete(DWORD, OPCHANDLE, HRESULT, DWORD, OPCHANDLE*, HRESULT*) override { failed=true; return E_FAIL; }
    HRESULT STDMETHODCALLTYPE OnCancelComplete(DWORD t, OPCHANDLE g) override {
        if (GetCurrentThreadId()!=owner || g!=expectedGroup || t!=302) { failed=true; return E_FAIL; }
        ++cancels; return S_OK;
    }
};
static void run(uint64_t writer) {
    Apartment apartment;
    {
        auto server = connect();
        Group group(server, false, 20, 41);
        const DWORD count = 4096;
        auto handles = group.add(count);
        ComPtr<Sink> a; a.Attach(new Sink(writer,count));
        ComPtr<Sink> b; b.Attach(new Sink(writer,count));
        // Prove that COM cannot shortcut callback apartment affinity through FTM.
        ComPtr<IMarshal> marshal;
        require(a.As(&marshal)==E_NOINTERFACE, "STA sink unexpectedly agile");
        DWORD ca=0, cb=0;
        checked(group.point->Advise(a.Get(), &ca), "Advise A RPC");
        checked(group.point->Advise(b.Get(), &cb), "Advise B RPC");
        DWORD cancel=0;
        require(FAILED(group.async->Refresh2(OPC_DS_CACHE, 7, &cancel)), "inactive refresh must fail");
        group.set(true,20,41);
        pump_until([&]{return (a->calls && b->calls) || a->failed || b->failed;});
        require(!a->failed && !b->failed, "two STA sinks initial4096");
        checked(group.async->SetEnable(FALSE), "disable periodic");
        // Synchronous 4096-item RPC also checks full native output ownership.
        OPCITEMSTATE* values=nullptr; HRESULT* errors=nullptr;
        checked(group.sync->Read(OPC_DS_CACHE,count,handles.data(),&values,&errors), "Sync Read4096 RPC");
        bool valid=values && errors;
        if (valid) for (DWORD i=0;i<count;++i) {
            try { a->sample(i,values[i].hClient,values[i].vDataValue,values[i].wQuality,values[i].ftTimeStamp,errors[i]); }
            catch (...) { valid=false; }
            VariantClear(&values[i].vDataValue);
        }
        CoTaskMemFree(values); CoTaskMemFree(errors); require(valid,"sync native4096 samples");
        VARIANT write{}; write.vt=VT_UI2; write.uiVal=999;
        errors=nullptr; checked(group.sync->Write(1,handles.data(),&write,&errors),"sync read-only Write RPC");
        require(errors && FAILED(errors[0]),"sync Write denied"); CoTaskMemFree(errors);
        cancel=99; errors=nullptr;
        checked(group.async->Write(1,handles.data(),&write,77,&cancel,&errors),"async read-only Write RPC");
        require(cancel==0 && errors && FAILED(errors[0]),"async Write early denied/no completion"); CoTaskMemFree(errors);
        // Explicit AsyncRead reaches both independent apartment-bound sinks.
        errors=nullptr;
        checked(group.async->Read(count,handles.data(),101,&cancel,&errors),"Async Read4096 RPC");
        valid=cancel!=0 && errors; if(errors) for(DWORD i=0;i<count;++i) valid=valid && SUCCEEDED(errors[i]);
        CoTaskMemFree(errors); require(valid,"AsyncRead admission");
        pump_until([&]{return (a->lastTransaction==101 && b->lastTransaction==101)||a->failed||b->failed;});
        require(!a->failed&&!b->failed&&a->reads==1&&b->reads==1,"two STA ReadComplete4096");
        require(FAILED(group.async->Cancel2(cancel)),"late cancel must fail");
        OPCHANDLE newClient=9000; errors=nullptr;
        checked(group.items->SetClientHandles(1,handles.data(),&newClient,&errors),"item client handle RPC");
        require(errors && SUCCEEDED(errors[0]),"item client update"); CoTaskMemFree(errors);
        a->changedHandle=b->changedHandle=true; a->expectedGroup=b->expectedGroup=42;
        group.set(true,30,42);
        checked(group.async->Refresh2(OPC_DS_DEVICE,201,&cancel),"Refresh DEVICE RPC");
        pump_until([&]{return (a->lastTransaction==201&&b->lastTransaction==201)||a->failed||b->failed;});
        require(!a->failed&&!b->failed,"refresh bits/state/handle");
        // CACHE gates group activity; DEVICE retains native producer quality.
        group.set(false,30,42);
        values=nullptr;errors=nullptr;
        checked(group.sync->Read(OPC_DS_CACHE,1,&handles[1],&values,&errors),"inactive CACHE RPC");
        valid=values&&errors&&SUCCEEDED(errors[0])&&values[0].wQuality==0x1c;
        if(values) VariantClear(&values[0].vDataValue); CoTaskMemFree(values);CoTaskMemFree(errors);
        require(valid,"inactive cache quality");
        group.set(true,30,42);
        checked(group.point->Unadvise(cb),"Unadvise B RPC");
        const unsigned bCalls=b->calls;
        // The sole sink worker is inside this callback. Its next Read completion
        // cannot start until we return, making successful queued cancellation deterministic.
        a->reentrant=[&](DWORD transaction) {
            if(transaction==301) {
                DWORD id=0; HRESULT* early=nullptr;
                checked(group.async->Read(count,handles.data(),302,&id,&early),"reentrant Read");
                bool good=id!=0&&early; if(early) for(DWORD i=0;i<count;++i) good=good&&SUCCEEDED(early[i]);
                CoTaskMemFree(early);require(good,"reentrant Read admission");
                checked(group.async->Cancel2(id),"queued Cancel RPC");
                require(FAILED(group.async->Cancel2(id)),"repeated cancel rejected");
            }
            if(transaction==401) {
                checked(group.point->Unadvise(ca),"reentrant Unadvise RPC");
                group.remove();
            }
        };
        checked(group.async->Refresh2(OPC_DS_DEVICE,301,&cancel),"cancel trigger refresh");
        pump_until([&]{return a->cancels==1||a->failed;});
        require(!a->failed&&a->reads==1&&b->calls==bCalls,"queued cancel delivered/no read completion/Unadvise isolation");
        checked(group.async->Refresh2(OPC_DS_DEVICE,401,&cancel),"reentrant teardown trigger");
        pump_until([&]{return group.removed||a->failed;});
        require(!a->failed&&group.removed,"reentrant RemoveGroup completed");
        a->reentrant={};
    }
    // >32 full external server connections/group lifetimes: retained native leases
    // or group/server slots must not accumulate across disconnect/reconnect.
    for(unsigned i=0;i<40;++i) {
        auto server=connect(); Group group(server,true,20,41); auto handles=group.add(3);
        OPCITEMSTATE* values=nullptr;HRESULT* errors=nullptr;
        checked(group.sync->Read(OPC_DS_DEVICE,3,handles.data(),&values,&errors),"reconnect Read RPC");
        bool valid=values&&errors;
        if(values&&errors) for(unsigned j=0;j<3;++j) { valid=valid&&SUCCEEDED(errors[j]);VariantClear(&values[j].vDataValue); }
        CoTaskMemFree(values);CoTaskMemFree(errors);require(valid,"reconnect samples");group.remove();
    }
    std::puts("Independent STA RPC: owner-thread two-sink callbacks,4096 items,native bits/clock/quality,state,Read/Refresh/Cancel,readonly Write,reentrant teardown,40 reconnects PASS");
}
int wmain(int argc,wchar_t** argv) {
    try { require(argc==2,"expected writer FILETIME");run(std::stoull(argv[1]));return 0; }
    catch(const std::exception& e) { std::fprintf(stderr,"STA client failure: %s\n",e.what());return 1; }
}
