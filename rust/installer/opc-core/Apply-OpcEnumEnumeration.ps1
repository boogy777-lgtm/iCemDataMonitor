[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceFile)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $SourceFile).Path
$expected = '00750842910e8f1fc0b793d1109bfc3b04434150773fe82699773796701b2daa'
if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $expected) { throw 'OpcSeLst.cpp differs from reviewed upstream input.' }
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$original = [IO.File]::ReadAllText($path, $utf8)
$newline = if ($original.Contains("`r`n")) { "`r`n" } else { "`n" }
function Replace-ExactlyOnce([string]$text, [string]$old, [string]$new) {
    $at = $text.IndexOf($old, [StringComparison]::Ordinal)
    if ($at -lt 0 -or $text.IndexOf($old, $at + $old.Length, [StringComparison]::Ordinal) -ge 0) { throw 'Expected unique upstream patch context not found.' }
    return $text.Substring(0, $at) + $new + $text.Substring($at + $old.Length)
}
function Replace-Scope([string]$text, [string]$startMarker, [string]$endMarker, [string]$replacement) {
    $start = $text.IndexOf($startMarker, [StringComparison]::Ordinal)
    if ($start -lt 0 -or $text.IndexOf($startMarker, $start + 1, [StringComparison]::Ordinal) -ge 0) { throw 'Expected unique upstream scope start.' }
    $end = $text.IndexOf($endMarker, $start, [StringComparison]::Ordinal)
    if ($end -le $start) { throw 'Expected upstream scope end after its start.' }
    return $text.Substring(0, $start) + $replacement + $text.Substring($end)
}

$helper = @'
// iCem: ATL CComEnum returns E_FAIL after Init(NULL, NULL), so an empty
// category result cannot be enumerated. Copy into a dynamically sized snapshot:
// an empty snapshot is S_FALSE/0, and storage owned by this object outlives the
// returned enumerator (the former vector was deleted at return).
template<class Interface, const IID* piid, class T>
HRESULT IcemCreateGuidEnum(const std::vector<GUID>& source, IUnknown** ppEnum)
{
	typedef CComObject<CComEnum<Interface, piid, T, _Copy<T> > > EnumType;
	*ppEnum = NULL;
	const size_t count = source.size();
	// ATL treats NULL/NULL as an uninitialized enumerator and returns E_FAIL,
	// so an empty result keeps a non-null begin marker with begin == end.
	const size_t allocation = count ? count : 1;
	T* items = new (std::nothrow) T[allocation];
	if (items == NULL)
		return E_OUTOFMEMORY;
	for (size_t index = 0; index < count; index++)
		items[index] = source[index];
	T* end = items + count;
	EnumType* enumerator = NULL;
	HRESULT hr = EnumType::CreateInstance(&enumerator);
	if (FAILED(hr))
	{
		delete[] items;
		return hr;
	}
	hr = enumerator->Init(items, end, NULL, AtlFlagTakeOwnership);
	if (FAILED(hr))
	{
		delete[] items;
		delete enumerator;
		return hr;
	}
	hr = enumerator->QueryInterface(*piid, reinterpret_cast<void**>(ppEnum));
	if (FAILED(hr))
		delete enumerator;
	return hr;
}

'@ -replace "`r?`n", $newline
$helper = $helper.TrimEnd()
$text = Replace-ExactlyOnce $original '#include "OpcSvLst.h"' ('#include "OpcSvLst.h"' + $newline + $newline + '#include <new>' + $newline + $newline + $helper)

$firstStart = "`t/*${newline}`tICatInformationPtr pCatInfo;"
$firstReplacement = @'
	IOPCEnumGUID* enumerated = NULL;
	HRESULT hr = EnumClassesOfCategories2(cImplemented, rgcatidImpl, cRequired, rgcatidReq, &enumerated);
	if (FAILED(hr))
		return hr;

	std::vector<GUID> guids;
	GUID guid;
	unsigned long count;
	while ((hr = enumerated->Next(1, &guid, &count)) == S_OK)
		guids.push_back(guid);
	enumerated->Release();
	if (FAILED(hr))
		return hr;

	return IcemCreateGuidEnum<IEnumCLSID, &IID_IEnumCLSID, CLSID>(guids, reinterpret_cast<IUnknown**>(ppenumClsid));
}


'@ -replace "`r?`n", $newline
$text = Replace-Scope $text $firstStart ('// EnumClassesOfCategories' + $newline + 'STDMETHODIMP COpcServerList::EnumClassesOfCategories(') $firstReplacement

# Anchor on the private overload signature: the public overload's dead comment
# block contains the same body text and must stay untouched (it only delegates).
$secondStart = "IOPCEnumGUID** ppenumClsid)${newline}{${newline}`t*ppenumClsid = 0;"
$secondReplacement = @'
IOPCEnumGUID** ppenumClsid)
{
	*ppenumClsid = 0;

	IOPCEnumGUID* enumerated = NULL;
	HRESULT hr = EnumClassesOfCategories2(cImplemented, rgcatidImpl, cRequired, rgcatidReq, &enumerated);
	if (FAILED(hr))
		return hr;

	std::vector<GUID> guids;
	GUID guid;
	unsigned long count;
	while ((hr = enumerated->Next(1, &guid, &count)) == S_OK)
	{
		guids.push_back(guid);
	}
	enumerated->Release();
	if (FAILED(hr))
	{
		return hr;
	}

	// need to fetch x64 only servers from the OPC Category Manager (call should fail on x86 systems).
	CComQIPtr<IOPCServerList> pList;

	hr = pList.CoCreateInstance(CLSID_OpcCategoryManager);

	if (SUCCEEDED(hr))
	{
		CComPtr<IEnumGUID> enumGuid2;

		hr = pList->EnumClassesOfCategories(cImplemented, rgcatidImpl, cRequired, rgcatidReq, &enumGuid2);
		
		if (SUCCEEDED(hr))
		{
			while ((hr = enumGuid2->Next(1, &guid, &count)) == S_OK)
			{
				bool found = false;

				for (UINT ii = 0; ii < guids.size(); ii++) 
				{
					if (IsEqualGUID(guids[ii], guid))
					{
						found = true;
						break;
					}
				}

				if (!found)
				{
					guids.push_back(guid);
				}
			}

			enumGuid2.Release();
		}

		pList.Release();
	}

	// Do we need to enumerate the old-style DA 1.0 servers?
	if (cImplemented != -1) 
	{
		for (ULONG i = 0; i < cImplemented; i++) 
		{
			if (IsEqualGUID(rgcatidImpl[i], CATID_OPCDAServer10)) 
			{
				AddOldstyleServers(&guids);
				break;
			}
		}
	}

	return IcemCreateGuidEnum<IOPCEnumGUID, &IID_IOPCEnumGUID, GUID>(guids, reinterpret_cast<IUnknown**>(ppenumClsid));
}


'@ -replace "`r?`n", $newline
$text = Replace-Scope $text $secondStart ('// AddOldstyleServers' + $newline + 'void COpcServerList::AddOldstyleServers') $secondReplacement

[IO.File]::WriteAllText($path, $text, $utf8)
Write-Output ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())
