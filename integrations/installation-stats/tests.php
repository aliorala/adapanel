<?php
declare(strict_types=1);
require_once __DIR__ . '/collector.php';
function check(bool $ok, string $label): void { if (!$ok) throw new RuntimeException($label); echo "PASS $label\n"; }
function empty_state(): array { return ['schemaVersion'=>1,'records'=>[],'daily'=>[]]; }
$now = 1792584000; $state = empty_state(); $token = str_repeat('a',64);
$report = ['schemaVersion'=>2,'installationToken'=>$token,'panelVersion'=>'0.8.117','shareDetails'=>false];
$legacyReport = $report; $legacyReport['schemaVersion']=1; unset($legacyReport['shareDetails']); $legacyReport['location']='Ankara'; $legacyReport['datacenter']='Self reported';
$legacyValidated=validate_report($legacyReport);
check(array_keys($legacyValidated)===['schemaVersion','installationToken','panelVersion','shareDetails'] && $legacyValidated['shareDetails']===true && $legacyValidated['schemaVersion']===1,'legacy metadata discarded');
foreach([1,2,3] as $schema){$bad=$schema===1?$legacyReport:$report;$bad['schemaVersion']=$schema;$bad['email']='forbidden';try{validate_report($bad);check(false,'unknown schema or fields rejected');}catch(InvalidArgumentException $e){check(true,'unknown schema or fields rejected');}}
$unknown=$report;$unknown['schemaVersion']=3;try{validate_report($unknown);check(false,'unsupported schema rejected');}catch(InvalidArgumentException $e){check(true,'unsupported schema rejected');}
$missing=$report;unset($missing['panelVersion']);try{validate_report($missing);check(false,'missing fields rejected');}catch(InvalidArgumentException $e){check(true,'missing fields rejected');}
accept_report($state,$report,'203.0.113.4',$now,'test-rate-secret');
accept_report($state,$report,'203.0.113.4',$now+86400,'test-rate-secret');
check(count($state['records'])===1 && summary($state,$now+86400)['activeInstallations']===1,'daily deduplication');
check(summary($state,$now+86400+ACTIVE_SECONDS-1)['activeInstallations']===1,'active before 48h boundary');
check(summary($state,$now+86400+ACTIVE_SECONDS)['activeInstallations']===0,'inactive at 48h boundary');
check(!str_contains(json_encode($state),$token),'raw token is not stored');
check(!str_contains(json_encode($state),'203.0.113.4'),'raw connection IP is not stored');
$legacyState=empty_state();accept_report($legacyState,$legacyReport,'203.0.113.4',$now,'test-rate-secret');
check(current($legacyState['records'])['shareDetails']===true && current($legacyState['records'])['ip']==='203.0.113.4' && !isset(current($legacyState['records'])['location']),'legacy opted-in report includes peer IP but discards client metadata');
$details=$report;$details['shareDetails']=true;$detailState=empty_state();accept_report($detailState,$details,'203.0.113.4',$now,'test');
check(current($detailState['records'])['ip']==='203.0.113.4','details enabled stores validated peer IP');
$id=array_key_first($detailState['records']);$detailState['records'][$id]['country']='OldCountry';$detailState['records'][$id]['city']='OldCity';
accept_report($detailState,$report,'203.0.113.4',$now+1,'test');
check($detailState['records'][$id]['ip']==='203.0.113.4' && $detailState['records'][$id]['country']==='OldCountry' && !$detailState['records'][$id]['shareDetails'] && $detailState['records'][$id]['lastSeen']===$now,'rapid off preserves details without extending activity');
$history=$detailState['records'][$id];$off=$report;$off['panelVersion']='0.8.999';accept_report($detailState,$off,'198.51.100.5',$now+86400,'test');
check(count($detailState['records'])===1 && $detailState['records'][$id]['version']==='0.8.999' && $detailState['records'][$id]['lastSeen']===$now+86400 && $detailState['records'][$id]['ip']===$history['ip'] && $detailState['records'][$id]['lastDetailsAt']===$history['lastDetailsAt'],'daily off updates version and activity with stable identity and historical details');
try{$bad=$report;$bad['shareDetails']='true';validate_report($bad);check(false,'nonboolean details rejected');}catch(InvalidArgumentException $e){check(true,'nonboolean details rejected');}
check(!str_contains(json_encode(summary($state,$now+86400)),'203.0.113.4'),'public summary excludes IP');
$other=$report; $other['installationToken']=str_repeat('b',64);
accept_report($state,$other,'203.0.113.4',$now+86400,'test-rate-secret');
check(count($state['records'])===2,'two installations behind one NAT');
$before=$state; accept_report($state,$other,'203.0.113.4',$now+86401,'test-rate-secret');
check($state===$before,'rapid replay does not extend activity');
prune_records($state,$now+86400+RETENTION_SECONDS); check(count($state['records'])===0,'30-day retention');
try { validate_report($report+['email'=>'not-allowed']); check(false,'unknown fields rejected'); } catch(InvalidArgumentException $e) { check(true,'unknown fields rejected'); }
$state=empty_state();
for($i=0;$i<20;$i++){ $report['installationToken']=hash('sha256',(string)$i);accept_report($state,$report,'203.0.113.4',$now,'test-rate-secret'); }
try { $report['installationToken']=hash('sha256','extra');accept_report($state,$report,'203.0.113.4',$now,'test-rate-secret');check(false,'registration cap'); }
catch(OverflowException $e){check(count($state['records'])===20,'registration cap');}
$dir=sys_get_temp_dir().'/adapanel-registry-'.bin2hex(random_bytes(8));mkdir($dir);
try {
 file_put_contents($dir.'/registry.json',json_encode(empty_state()));
 with_store($dir,function(array &$s)use($report,$now){accept_report($s,$report,'203.0.113.9',$now,'test');});
 $result=with_store($dir,fn(array &$s)=>summary($s,$now));check($result['activeInstallations']===1,'atomic persisted store');
 $legacy=json_decode(file_get_contents($dir.'/registry.json'),true);
 $legacyId=array_key_first($legacy['records']);
 unset($legacy['records'][$legacyId]['shareDetails']);$legacy['records'][$legacyId]['ip']='203.0.113.9';
 $legacy['records'][$legacyId]['location']='LegacyCity';$legacy['records'][$legacyId]['datacenter']='LegacyDC';
 file_put_contents($dir.'/registry.json',json_encode($legacy));
 $migrated=with_store($dir,fn(array &$s)=>$s);
 $legacy['records'][$legacyId]['shareDetails']=false;$legacy['records'][$legacyId]['lastDetailsAt']=$legacy['records'][$legacyId]['lastSeen'];
 check($migrated===$legacy,'legacy metadata preserved and timestamped as history');
 check(json_decode(file_get_contents($dir.'/registry.json'),true)===$legacy,'read persists legacy historical metadata');
 $beforeMigrationBytes=file_get_contents($dir.'/registry.json');
 with_store($dir,fn(array &$s)=>$s);
 check(file_get_contents($dir.'/registry.json')===$beforeMigrationBytes,'metadata migration is idempotent');
 file_put_contents($dir.'/registry.json','corrupt');
 try {with_store($dir,fn(array &$s)=>summary($s,$now));check(false,'corrupt store fails closed');}
 catch(JsonException $e){check(true,'corrupt store fails closed');}
} finally {foreach(glob($dir.'/*') as $p)unlink($p);rmdir($dir);}

$calls=0;$fake=function(string $url)use(&$calls):string{$calls++;check(str_starts_with($url,'https://ipwho.is/'),'geo provider is HTTPS');return '{"success":true,"country":"Türkiye","city":"Ankara"}';};
check(lookup_geolocation('127.0.0.1',$fake)===[] && $calls===0,'private IP never reaches provider');
check(lookup_geolocation('8.8.8.8',$fake)===['country'=>'Türkiye','city'=>'Ankara'] && $calls===1,'geo result validated using injected provider');
check(lookup_geolocation('8.8.8.8',fn($url)=>throw new RuntimeException('test'))===[],'geo provider failure is nonfatal');
check(lookup_geolocation('8.8.8.8',fn($url)=>str_repeat('x',4097))===[],'oversized geo response discarded');
$geoState=empty_state();accept_report($geoState,$details,'8.8.8.8',$now,'test');$id=array_key_first($geoState['records']);
$ticket=reserve_geolocation($geoState,$id,$now);check($ticket!==null,'lookup slot reserved for accepted report');
apply_geolocation($geoState,$ticket,['country'=>'Türkiye','city'=>'Ankara']);
check($geoState['records'][$id]['city']==='Ankara','location applied to matching record');
check(reserve_geolocation($geoState,$id,$now+86400-1)===null,'geo attempt cached for 24 hours');
$offReport=$details;$offReport['shareDetails']=false;accept_report($geoState,$offReport,'8.8.8.8',$now+1,'test');apply_geolocation($geoState,$ticket,['country'=>'ShouldNotReturn']);
check($geoState['records'][$id]['country']==='Türkiye' && reserve_geolocation($geoState,$id,$now+86400)===null,'off race preserves historic geo and cannot apply new result or trigger lookup');
$history=$geoState['records'][$id];accept_report($geoState,$details,'1.1.1.1',$now+86400,'test');$ticket=reserve_geolocation($geoState,$id,$now+86400);apply_geolocation($geoState,$ticket,[]);
check($geoState['records'][$id]['ip']==='1.1.1.1' && $geoState['records'][$id]['geoIp']==='8.8.8.8' && $geoState['records'][$id]['city']==='Ankara' && $geoState['records'][$id]['geoUpdatedAt']===$history['geoUpdatedAt'],'new IP with failed lookup preserves separately timestamped historical geo');
$quota=empty_state();accept_report($quota,$details,'8.8.8.8',$now,'test');$quota['geoDaily'][gmdate('Y-m-d',$now)]=900;
check(reserve_geolocation($quota,array_key_first($quota['records']),$now)===null,'daily provider quota capped at 900');
