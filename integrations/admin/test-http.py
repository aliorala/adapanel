"""Run actual PHP authentication against isolated files; never sends test records to production."""
from pathlib import Path
import tempfile, subprocess, socket, time, json, shutil, urllib.request, urllib.error, hashlib

ROOT=Path(__file__).resolve().parents[2]
PHP=ROOT/'.tools/php-usage-tests/php.exe'

def password_hash(password):
    return subprocess.check_output([str(PHP),'-r','echo password_hash(stream_get_contents(STDIN), PASSWORD_BCRYPT, ["cost"=>12]);'],input=password.encode()).decode()

class Client:
    def __init__(self, base): self.base=base;self.cookie='';self.csrf='';self.headers=None
    def call(self,action,body=None,headers=None,method=None):
        extra={'Cookie':self.cookie,'Origin':'https://adapanel.host','X-CSRF-Token':self.csrf,'Content-Type':'application/json'}
        extra.update(headers or {})
        request=urllib.request.Request(self.base+'/admin/api.php?action='+action,json.dumps(body).encode() if body is not None else None,extra,method=method)
        try: response=urllib.request.urlopen(request,timeout=10)
        except urllib.error.HTTPError as e: response=e
        with response:
            self.headers=response.headers
            for cookie in response.headers.get_all('Set-Cookie',[]): self.cookie=cookie.split(';',1)[0]
            data=json.load(response)
            if 'csrf' in data:self.csrf=data['csrf']
            return response.status,data
    def login(self,password):
        self.call('session');return self.call('login',{'username':'admin','password':password})

def main():
    with tempfile.TemporaryDirectory(prefix='adapanel-admin-test-') as directory:
        folder=Path(directory);private=folder/'App_Data/installation-stats';private.mkdir(parents=True);(private/'sessions').mkdir()
        shutil.copytree(ROOT/'integrations/admin',folder/'admin')
        shutil.copytree(ROOT/'integrations/installation-stats',folder/'installation-stats')
        old='Temporary-Password-42!';new='Şifre6'
        original={'schemaVersion':1,'records':{'a'*64:{'id':'a'*64,'ip':'127.0.0.1','version':'0.8.117','location':'<script>test</script>','datacenter':'Test','firstSeen':int(time.time()),'lastSeen':int(time.time())}},'daily':{}}
        (private/'registry.json').write_text(json.dumps(original))
        (private/'admin-auth.json').write_text(json.dumps({'passwordHash':password_hash(old),'version':'v1','attempts':{}}))
        (folder/'router.php').write_text("<?php $_SERVER['HTTPS']='on'; $p=parse_url($_SERVER['REQUEST_URI'],PHP_URL_PATH); if($p==='/admin/api.php'){require __DIR__.$p;return true;} http_response_code(404);")
        with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
        process=subprocess.Popen([str(PHP),'-S',f'127.0.0.1:{port}','router.php'],cwd=folder,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        try:
            a=Client(f'http://127.0.0.1:{port}');b=Client(a.base)
            for _ in range(40):
                try:a.call('session');break
                except OSError:time.sleep(.1)
            cookie=' '.join(a.headers.get_all('Set-Cookie',[])).lower()
            assert 'secure' in cookie and 'httponly' in cookie and 'samesite=strict' in cookie and 'path=/admin' in cookie
            assert a.call('stats')[0]==401
            assert a.call('login',{'username':'admin','password':old},{'X-CSRF-Token':'bad'})[0]==403
            assert a.call('login',{'username':'admin','password':old},{'Origin':'https://evil.example'})[0]==403
            assert a.call('login',{'username':'admin','password':old},{'Content-Type':'text/plain'})[0]==415
            assert a.call('login',method='GET')[0]==405
            assert a.call('login',{'username':'admin','password':'wrong'})==(401,{'error':'invalid_credentials'})
            before=a.cookie
            assert a.login(old)[0]==200 and a.cookie!=before
            stats=a.call('stats')[1]
            assert stats['activeInstallations']==1
            assert set(stats['installations'][0])=={'id','version','firstSeen','lastSeen','shareDetails','ip','location','datacenter','lastDetailsAt'}
            assert stats['installations'][0]['ip']=='127.0.0.1' and stats['installations'][0]['shareDetails'] is False
            assert b.login(old)[0]==200
            assert a.call('password',{'currentPassword':'wrong','newPassword':new,'confirmation':new})==(401,{'error':'wrong_current_password'})
            assert a.call('password',{'currentPassword':old,'newPassword':'short','confirmation':'short'})[0]==400
            assert a.call('password',{'currentPassword':old,'newPassword':'ş'*5,'confirmation':'ş'*5})[0]==400
            assert a.call('password',{'currentPassword':old,'newPassword':new,'confirmation':'mismatch'})[0]==400
            assert a.call('password',{'currentPassword':old,'newPassword':old,'confirmation':old})==(400,{'error':'same_password'})
            before=a.cookie
            assert a.call('password',{'currentPassword':old,'newPassword':new,'confirmation':new})[0]==200
            assert a.cookie!=before and a.call('stats')[0]==200
            assert b.call('stats')[0]==401
            assert b.login(old)[0]==401 and b.login(new)[0]==200
            stored=json.loads((private/'admin-auth.json').read_text());assert stored['passwordHash'].startswith('$2y$') and new not in json.dumps(stored)
            original['records']['a'*64]['lastDetailsAt']=original['records']['a'*64]['lastSeen']
            original['records']['a'*64]['shareDetails']=False
            original['daily']=[]  # PHP encodes an empty associative map as an array when migrating.
            assert json.loads((private/'registry.json').read_text())==original
            old_cookie=a.cookie;assert a.call('logout',{})[0]==200
            a.cookie=old_cookie;assert a.call('stats')[0]==401
            c=Client(a.base)
            for _ in range(8):assert c.login('wrong')[0]==401
            assert c.login(new)[0]==429
            (private/'admin-auth.json').write_text('broken')
            assert b.call('stats')==(503,{'error':'temporarily_unavailable'})
            print('PASS: secure cookies, anonymous denial, CSRF, origin, method/content type, login, session rotation, password policy/change, old password denial, other-session revocation, logout replay, rate limit, private records preserved, corruption fails closed')
        finally:process.terminate();process.wait(timeout=10)

if __name__=='__main__':main()
