"""Synthetic-fixture latency harness; no Windows/MMF or production acceptance claim.
Run separately for baseline and candidate binaries on the same idle machine.
"""
import argparse, concurrent.futures, http.client, json, pathlib, platform, socket, statistics, struct, subprocess, tempfile, time

def freeport():
    with socket.socket() as s:
        s.bind(('127.0.0.1',0)); return s.getsockname()[1]
def exact(s,n):
    b=b''
    while len(b)<n:
        c=s.recv(n-len(b))
        if not c: raise RuntimeError('short Modbus response')
        b+=c
    return b
def stats(a):
    a=sorted(a)
    return {'samples':len(a),'p50_us':round(a[len(a)//2],2),'p95_us':round(a[min(len(a)-1,int(len(a)*.95))],2),'p99_us':round(a[min(len(a)-1,int(len(a)*.99))],2),'mean_us':round(statistics.mean(a),2),'raw_us':a}
def modbus(port,n,warm):
    out=[]
    with socket.create_connection(('127.0.0.1',port),timeout=5) as s:
        s.setsockopt(socket.IPPROTO_TCP,socket.TCP_NODELAY,1)
        for i in range(n+warm):
            q=struct.pack('>HHHBBHH',i%65536,0,6,1,3,0,100)
            t=time.perf_counter_ns(); s.sendall(q); h=exact(s,6); b=exact(s,int.from_bytes(h[4:6],'big'));elapsed=(time.perf_counter_ns()-t)/1000
            assert h[:4]==q[:4] and b[:3]==bytes([1,3,200]),(h,b[:4])
            if i>=warm:out.append(elapsed)
    return stats(out)
def web(port,n,warm):
    out=[];c=http.client.HTTPConnection('127.0.0.1',port,timeout=5)
    try:
        for i in range(n+warm):
            t=time.perf_counter_ns();c.request('POST','/icemdatamonitor.v1.TelemetryService/GetRegisters',b'\0'*5,{'Content-Type':'application/grpc-web+proto','X-Grpc-Web':'1'});r=c.getresponse();b=r.read();elapsed=(time.perf_counter_ns()-t)/1000
            assert r.status==200 and b'grpc-status:0' in b,(r.status,b[-100:])
            if i>=warm:out.append(elapsed)
    finally:c.close()
    return stats(out)
def run(a):
    rounds=[]
    for _ in range(a.rounds):
        p1,p2=freeport(),freeport()
        with tempfile.TemporaryDirectory() as t:
            log=open(pathlib.Path(t)/'host.log','w+')
            p=subprocess.Popen([a.binary,'--modbus-fixture',a.fixture,'--bind',f'127.0.0.1:{p1}','--web-bind',f'127.0.0.1:{p2}','--duration-ms','120000','--theme-file',str(pathlib.Path(t)/'theme')],stdout=log,stderr=log)
            try:
                deadline=time.monotonic()+8
                while True:
                    try:
                        with socket.create_connection(('127.0.0.1',p2),timeout=.1):pass
                        break
                    except OSError:
                        if p.poll() is not None or time.monotonic()>deadline:
                            log.seek(0);raise RuntimeError(log.read())
                        time.sleep(.02)
                time.sleep(.4)
                with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
                    m=pool.submit(modbus,p1,a.requests,a.warmup);w=pool.submit(web,p2,a.requests,a.warmup)
                    rounds.append({'modbus_fc03_100_words':m.result(),'grpc_web_getregisters_all':w.result()})
            finally:
                p.terminate()
                try:p.wait(timeout=3)
                except subprocess.TimeoutExpired:p.kill();p.wait()
                log.close()
    return {'binary':a.binary,'fixture':a.fixture,'rounds':rounds,'warmup_per_client':a.warmup,'concurrency':'one persistent Modbus + one persistent grpc-Web HTTP1 client; host synthetic fixture, no MMF','clock':'perf_counter_ns roundtrip','platform':platform.system()+' '+platform.machine()}
if __name__ == '__main__':
    p=argparse.ArgumentParser();p.add_argument('--binary',required=True);p.add_argument('--fixture',required=True);p.add_argument('--requests',type=int,default=1000);p.add_argument('--warmup',type=int,default=100);p.add_argument('--rounds',type=int,default=3);p.add_argument('--output',required=True);a=p.parse_args();result=run(a);pathlib.Path(a.output).write_text(json.dumps(result,indent=2)+'\n');print('Network measurements saved:', a.output)
