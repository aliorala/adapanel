"""Exercise the actual PHP endpoints against isolated local state, never production."""
from pathlib import Path
import json, shutil, tempfile, subprocess, socket, time, urllib.request, urllib.error, hashlib

ROOT = Path(__file__).resolve().parent
PHP = ROOT.parents[1] / '.tools/php-usage-tests/php.exe'
def request(base, path, method='GET', data=None, headers=None):
    raw = json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request(base+path, raw, headers or {}, method=method)
    try:
        with urllib.request.urlopen(req, timeout=5) as r: return r.status, json.load(r)
    except urllib.error.HTTPError as e: return e.code, json.load(e)

with tempfile.TemporaryDirectory(prefix='adapanel-stats-http-') as folder:
    folder=Path(folder); public=folder/'installation-stats'; private=folder/'App_Data/installation-stats'
    public.mkdir(); private.mkdir(parents=True)
    for p in ROOT.glob('*.php'):
        if p.name != 'tests.php': shutil.copyfile(p,public/p.name)
    key='f'*64
    (private/'config.json').write_text(json.dumps({'adminHash':hashlib.sha256(key.encode()).hexdigest(),'rateKey':'test-key-'*8}))
    (private/'registry.json').write_text(json.dumps({'schemaVersion':1,'records':{},'daily':{}}))
    # Local-only test adapter. Production refuses HTTP; it has no such override.
    (folder/'router.php').write_text("<?php $_SERVER['HTTPS']='on'; $p=parse_url($_SERVER['REQUEST_URI'],PHP_URL_PATH); if(str_starts_with($p,'/installation-stats/') && str_ends_with($p,'.php')) {require __DIR__.$p;return true;}http_response_code(404);")
    with socket.socket() as s: s.bind(('127.0.0.1',0));port=s.getsockname()[1]
    process=subprocess.Popen([str(PHP),'-S',f'127.0.0.1:{port}','router.php'],cwd=folder,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    base=f'http://127.0.0.1:{port}'
    try:
        for i in range(30):
            try:
                result=request(base,'/installation-stats/summary.php');break
            except OSError: time.sleep(.1)
        assert result[0]==410
        assert request(base,'/installation-stats/admin-api.php')[0]==410
        assert request(base,'/installation-stats/heartbeat.php')[0]==405
        assert request(base,'/installation-stats/heartbeat.php','POST',{}, {'Content-Type':'text/plain'})[0]==415
        assert request(base,'/installation-stats/heartbeat.php','POST',{}, {'Content-Type':'application/json'})[0]==400
        report={'schemaVersion':1,'installationToken':'b'*64,'panelVersion':'0.8.117','location':'<script>alert(1)</script>','datacenter':'Test'}
        assert request(base,'/installation-stats/heartbeat.php','POST',report,{'Content-Type':'application/json','X-Forwarded-For':'8.8.8.8'})==(200,{'schemaVersion':1,'accepted':True})
        modern={'schemaVersion':2,'installationToken':report['installationToken'],'panelVersion':'0.8.121','shareDetails':False}
        assert request(base,'/installation-stats/heartbeat.php','POST',modern,{'Content-Type':'application/json'})==(200,{'schemaVersion':2,'accepted':True})
        for bad in [dict(modern,email='forbidden'),dict(modern,location='forbidden'),dict(report,email='forbidden'),dict(modern,schemaVersion=3)]:
            assert request(base,'/installation-stats/heartbeat.php','POST',bad,{'Content-Type':'application/json'})[0]==400
        assert request(base,'/installation-stats/summary.php')[0]==410
        assert request(base,'/installation-stats/admin-api.php',headers={'Authorization':'Bearer '+key})[0]==410
        stored=json.loads((private/'registry.json').read_text())
        assert len(stored['records'])==1
        assert set(next(iter(stored['records'].values())))=={'id','version','firstSeen','lastSeen','shareDetails','connectionIp','connectionIpAt'}
        assert '8.8.8.8' not in json.dumps(stored)  # Forwarded header is never trusted.
        assert report['installationToken'] not in json.dumps(stored)
        modern['installationToken']='c'*64;modern['shareDetails']=True
        assert request(base,'/installation-stats/heartbeat.php','POST',modern,{'Content-Type':'application/json'})==(200,{'schemaVersion':2,'accepted':True})
        stored=json.loads((private/'registry.json').read_text()); detail=stored['records'][hashlib.sha256(('c'*64).encode()).hexdigest()]
        assert 'ip' not in detail and detail['connectionIp']=='127.0.0.1' and detail['shareDetails'] is True
        modern['shareDetails']=False
        assert request(base,'/installation-stats/heartbeat.php','POST',modern,{'Content-Type':'application/json'})[0]==200
        stored=json.loads((private/'registry.json').read_text());assert 'ip' not in stored['records'][detail['id']] and stored['records'][detail['id']]['connectionIp']=='127.0.0.1' and stored['records'][detail['id']]['shareDetails'] is False
        (private/'registry.json').write_text('corrupt')
        assert request(base,'/installation-stats/heartbeat.php','POST',report,{'Content-Type':'application/json'})==(503,{'error':'temporarily_unavailable'})
        print('PASS: HTTP auth, methods, content type, validation, deduplication, summary isolation, untrusted forwarding headers, corruption failure')
    finally:
        process.terminate();process.wait(timeout=10)
