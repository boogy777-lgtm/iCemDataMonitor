#!/usr/bin/env python3
"""Independent UA wire smoke; optional dependency asyncua==2.1.0.

Run from repository root; --host and --fixture must identify existing files.
Only child processes and temporary fixture/config files are changed.
"""
import argparse,asyncio,json,pathlib,platform,shutil,socket,struct,subprocess,tempfile,time
import asyncua
from asyncua import Client, ua

def stop(process):
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=3)


async def verify(endpoint, writer_ticks):
 async with Client(endpoint,timeout=4) as c:
  ns=await c.get_namespace_index('urn:icem:bus:v1')
  names=['var0','var4','var200','var400','var600','COM_writer_ticks','LAN_writer_ticks','RAW_writer_ticks','capture_generation','quality_scope']
  nodes=[c.get_node(ua.NodeId(name,ns)) for name in names]
  params=ua.ReadParameters();params.TimestampsToReturn=ua.TimestampsToReturn.Both
  params.NodesToRead=[ua.ReadValueId(NodeId=n.nodeid,AttributeId=ua.AttributeIds.Value) for n in nodes]
  data=await c.uaclient.read(params)
  assert [data[i].Value.VariantType for i in [0,1,2,3,4]]==[ua.VariantType.UInt16,ua.VariantType.Int32,ua.VariantType.Float,ua.VariantType.Float,ua.VariantType.UInt16]
  assert data[1].Value.Value==1700000000
  assert int.from_bytes(struct.pack('>f',data[2].Value.Value),'big')==0x7fc01234
  assert int.from_bytes(struct.pack('>f',data[3].Value.Value),'big')==0x80000000
  assert all(d.SourceTimestamp is None and d.ServerTimestamp is not None for d in data)
  assert data[2].StatusCode == ua.StatusCode(ua.StatusCodes.BadOutOfService)
  assert data[3].StatusCode == ua.StatusCode(ua.StatusCodes.Good)
  assert [data[i].Value.VariantType for i in [5,6,7]] == [ua.VariantType.Int64]*3
  assert [data[i].Value.Value for i in [5,6,7]] == writer_ticks
  assert data[8].Value.Value>0
  assert 'not_measurement_freshness' in data[9].Value.Value
  folder=c.get_node(ua.NodeId('Bus',ns)); children=await folder.get_children()
  assert len(children)==368,len(children)
  expected_ids={f'var{i}' for i in [*range(4),*range(4,600,2),*range(600,650)]}
  actual_ids={node.nodeid.Identifier for node in children}
  assert {name for name in actual_ids if isinstance(name,str) and name.startswith('var')}==expected_ids
  before=data[1].Value.Value
  await asyncio.sleep(.25)
  assert await nodes[1].read_value()==before
  try:await nodes[2].write_value(ua.Variant(99.,ua.VariantType.Float))
  except ua.UaStatusCodeError:pass
  else:raise AssertionError('writable source variable')
  class Handler:
   def __init__(self):self.event=asyncio.Event()
   def datachange_notification(self,node,value,data):self.event.set()
  h=Handler();sub=await c.create_subscription(100,h,queue_maxsize=4)
  await sub.subscribe_data_change(nodes[1],queuesize=1)
  await asyncio.wait_for(h.event.wait(),3)
  await sub.delete()
  return {'capture_generation':data[8].Value.Value,'namespace':ns,'browse_variables':len(children),'types':['UInt16','Int32','Float','Float','UInt16'],'nan_bits':'7fc01234','negative_zero_bits':'80000000','source_timestamps':'absent','server_timestamps':'present','inactive_LAN':'BadOutOfService','active_RAW':'Good','read_only':True,'subscription_initial_notification':True,'COM_time':before,'writer_ticks':{names[i]:data[i].Value.Value for i in [5,6,7]}}

def main(a):
 startup_seconds,test_seconds=15,15
 host_duration_ms=(startup_seconds+test_seconds+5)*1000
 pathlib.Path(a.output).parent.mkdir(parents=True,exist_ok=True)
 a.host=str(pathlib.Path(a.host).resolve())
 a.fixture=str(pathlib.Path(a.fixture).resolve())
 with tempfile.TemporaryDirectory(prefix='icem-ua-independent-') as t:
  t=pathlib.Path(t)
  with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
  with socket.socket() as s:s.bind(('127.0.0.1',0));mb=s.getsockname()[1]
  cfg=t/'ua.json';cfg.write_text(json.dumps({'bind':f'127.0.0.1:{port}','profile':'loopback-anonymous-none','update_interval_ms':100}))
  b=bytearray(pathlib.Path(a.fixture).read_bytes())
  for addr,bits in [(200,0x7fc01234),(400,0x80000000)]:
   b[64+addr*2:68+addr*2]=struct.pack('>HH',bits&65535,bits>>16)
  fixture=t/'bus.bin';fixture.write_bytes(b)
  with (t/'host.log').open('w+') as log:
   p=subprocess.Popen([a.host,'--modbus-fixture',str(fixture),'--bind',f'127.0.0.1:{mb}','--duration-ms',str(host_duration_ms),'--opcua-config',str(cfg)],stdout=log,stderr=log,cwd=t)
   try:
    deadline=time.monotonic()+startup_seconds
    while True:
     try:
      with socket.create_connection(('127.0.0.1',port),timeout=.1):break
     except OSError:
      if p.poll() is not None or time.monotonic()>deadline:log.seek(0);raise RuntimeError(log.read())
      time.sleep(.02)
    async def sessions():
     writer_ticks=[struct.unpack_from('<q',b,offset)[0] for offset in [10,19,28]]
     first=await verify(f'opc.tcp://127.0.0.1:{port}/',writer_ticks)
     reconnected=await verify(f'opc.tcp://127.0.0.1:{port}/',writer_ticks)
     assert reconnected['COM_time']==first['COM_time']
     assert reconnected['writer_ticks']==first['writer_ticks']
     assert reconnected['capture_generation']>=first['capture_generation']
     return dict(first,reconnected_session=True,reconnect_generation=reconnected['capture_generation'])
    first=asyncio.run(asyncio.wait_for(sessions(),test_seconds))
    assert p.wait(timeout=host_duration_ms/1000+5)==0
    result={'result':'PASS','client':'asyncua '+asyncua.__version__,'platform':platform.platform(),'python_version':platform.python_version(),'scope':f'independent Python UA client -> real Rust UA server, {platform.system()} loopback fixture, no live MMF/hardware claim','checks':first,'host_exit':0}
    pathlib.Path(a.output).write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
   except Exception as error:
    pathlib.Path(a.output).write_text(json.dumps({'result':'FAIL','platform':platform.platform(),'error':str(error),'host_exit':p.poll()},indent=2)+'\n')
    log.flush();log.seek(0);print(log.read());raise
   finally:
    stop(p)
    log.flush();shutil.copyfile(t/'host.log',pathlib.Path(a.output).with_suffix('.host.log'))
if __name__ == '__main__':
 p=argparse.ArgumentParser()
 p.add_argument('--host',required=True)
 p.add_argument('--fixture',required=True)
 p.add_argument('--output',required=True)
 main(p.parse_args())
