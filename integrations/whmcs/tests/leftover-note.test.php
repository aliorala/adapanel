<?php

/**
 * AdaPanel WHMCS modulu -- kalinti notu davranis testi.
 *
 * NEDEN VAR: bu depoda gercek bir WHMCS kurulumu YOK, yani modul uctan uca calistirilamiyor.
 * `php -l` yalnizca sozdizimini olcer; "kalinti listesi gercekten okundu mu, not gercekten
 * uretildi mi, cagri basarisiz oldugunda sessizce yutuluyor mu" sorularini olcmez. Bu betik
 * modulun WHMCS'e BAGLI OLMAYAN karar noktalarini gercekten CALISTIRIR: `localAPI` ve
 * `logModuleCall` burada birer casus olarak tanimlaniyor ve cagrilar sayiliyor.
 *
 * Calistirma:  php integrations/whmcs/tests/leftover-note.test.php
 * Cikis kodu:  0 = gecti, 1 = kaldi.
 */

define('WHMCS', true);

$GLOBALS['ada_test'] = array(
    'localApi' => array(),
    'moduleLog' => array(),
    'localApiResult' => array('result' => 'success', 'noteid' => 7),
);

function localAPI($command, $values, $adminuser = '')
{
    $GLOBALS['ada_test']['localApi'][] = array(
        'command' => $command,
        'values' => $values,
        'admin' => $adminuser,
    );

    return $GLOBALS['ada_test']['localApiResult'];
}

function logModuleCall($module, $action, $request, $response, $processed = '', $replace = array())
{
    $GLOBALS['ada_test']['moduleLog'][] = array(
        'action' => $action,
        'processed' => is_string($processed) ? $processed : json_encode($processed),
    );
}

require_once __DIR__ . '/../adapanel/adapanel.php';

$basarisiz = 0;

function ada_assert($kosul, $mesaj)
{
    global $basarisiz;
    if ($kosul) {
        echo "  gecti : $mesaj\n";
        return;
    }

    echo "  KALDI : $mesaj\n";
    $basarisiz++;
}

function ada_sifirla($localApiResult = null)
{
    $GLOBALS['ada_test']['localApi'] = array();
    $GLOBALS['ada_test']['moduleLog'] = array();
    if ($localApiResult !== null) {
        $GLOBALS['ada_test']['localApiResult'] = $localApiResult;
    }
}

/** WHMCS'in modul islevlerine verdigi parametrelerin bu testte gereken kismi. */
function ada_params()
{
    return array(
        'username' => 'musteri1',
        'domain' => 'ornek.com',
        'serviceid' => 42,
        'userid' => 9,
        'serveraccesshash' => 'entry=panel;admin=whmcsadmin',
    );
}

/**
 * Panelin GERCEK yanit zarfi (UserDeletionResultDto -> camelCase).
 * Alan adlari C# DTO'sundan alindi: userDeleted / siteRemovalRequested / deletedSites /
 * leftovers[{siteName, kind, name, reason, deliberate}] / completed.
 */
function ada_basari_zarfi()
{
    return array(
        'success' => true,
        'message' => 'users.delete.success',
        'data' => array(
            'userId' => '11111111-1111-1111-1111-111111111111',
            'userName' => 'musteri1',
            'userDeleted' => true,
            'siteRemovalRequested' => false,
            'deletedSites' => array(),
            'leftovers' => array(
                array(
                    'siteName' => 'ornek.com',
                    'kind' => 'site',
                    'name' => 'ornek.com',
                    'reason' => 'removeSites=false: site sunucuda birakildi',
                    'deliberate' => true,
                ),
                array(
                    'siteName' => 'ornek.com',
                    'kind' => 'database',
                    'name' => 'mysql:ornek_db',
                    'reason' => 'sahiplik kanitlanamadi',
                    'deliberate' => true,
                ),
            ),
            'completed' => true,
        ),
    );
}

echo "1) removeSites=false yolunda (panel 200 + success:true) kalintilar NOTA yazilir\n";
ada_sifirla(array('result' => 'success', 'noteid' => 7));
adapanel_recordLeftovers(ada_params(), ada_basari_zarfi(), array('userName' => 'musteri1'));

$cagrilar = $GLOBALS['ada_test']['localApi'];
ada_assert(count($cagrilar) === 1, 'tam olarak bir WHMCS API cagrisi yapildi');
ada_assert($cagrilar[0]['command'] === 'AddClientNote', 'kullanilan uc AddClientNote (uzerine yazan UpdateClientProduct DEGIL)');
ada_assert((int) $cagrilar[0]['values']['userid'] === 9, 'not, hizmetin MUSTERISINE yazildi');
ada_assert(!empty($cagrilar[0]['values']['sticky']), 'not sticky: musteri hesabinda ve biletlerde gorunur');
ada_assert($cagrilar[0]['admin'] === 'whmcsadmin', 'localAPI Access Hash icindeki admin ile cagrildi');

$not = (string) $cagrilar[0]['values']['notes'];
ada_assert(strpos($not, 'mysql:ornek_db') !== false, 'kalintinin ADI notta yazili');
ada_assert(strpos($not, 'sahiplik kanitlanamadi') !== false, 'kalintinin SEBEBI notta yazili');
ada_assert(strpos($not, '[bilerek birakildi]') !== false, 'bilerek birakilanlar ISARETLI (ariza sanilmasin)');
ada_assert(strpos($not, 'Hizmet no: 42') !== false, 'hangi WHMCS hizmeti oldugu notta yazili');
ada_assert(count($GLOBALS['ada_test']['moduleLog']) >= 1, 'modul gunlugune de yazildi (not silinse bile iz kalir)');

echo "2) BASARISIZ kalinti [BASARISIZ] diye isaretlenir ve 'tamamlandi' YALAN SOYLEMEZ\n";
ada_sifirla();
$yarim = ada_basari_zarfi();
$yarim['data']['leftovers'][1]['deliberate'] = false;
$yarim['data']['completed'] = false;
adapanel_recordLeftovers(ada_params(), $yarim, array('userName' => 'musteri1'));
$not = (string) $GLOBALS['ada_test']['localApi'][0]['values']['notes'];
ada_assert(strpos($not, '[BASARISIZ]') !== false, 'basarisiz kalinti bilerek birakilandan AYIRT EDILIYOR');
ada_assert(strpos($not, 'eksiksiz tamamladi mi: HAYIR') !== false, 'yarim is notta acikca yaziyor');

echo "3) Kalinti YOKSA not YAZILMAZ (notlar gurultuye bogulmaz)\n";
ada_sifirla();
$temiz = ada_basari_zarfi();
$temiz['data']['leftovers'] = array();
adapanel_recordLeftovers(ada_params(), $temiz, array('userName' => 'musteri1'));
ada_assert(count($GLOBALS['ada_test']['localApi']) === 0, 'temiz silmede hicbir not yazilmadi');
ada_assert(count($GLOBALS['ada_test']['moduleLog']) === 0, 'temiz silmede gunluge de yazilmadi');

echo "4) Not YAZILAMAZSA sessizce yutulmaz: metin modul gunlugune duser\n";
ada_sifirla(array('result' => 'error', 'message' => 'Invalid admin username'));
$yazildi = adapanel_addClientNote(ada_params(), 'deneme notu');
ada_assert($yazildi === false, 'basarisiz cagri BASARILI sayilmiyor (sonuc OLCULUYOR)');
$gunluk = $GLOBALS['ada_test']['moduleLog'];
ada_assert(count($gunluk) === 1, 'basarisizlik gunluge yazildi');
ada_assert(strpos($gunluk[0]['processed'], 'deneme notu') !== false, 'kaybolan notun METNI gunlukte duruyor');

echo "5) Hata zarfindaki (409) kalintilar da okunur\n";
ada_sifirla(array('result' => 'success', 'noteid' => 8));
$hata = array(
    'success' => false,
    'message' => 'users.delete.incomplete',
    'data' => array(
        'userName' => 'musteri1',
        'userDeleted' => true,
        'siteRemovalRequested' => true,
        'deletedSites' => array('eski.com'),
        'leftovers' => array(
            array(
                'siteName' => 'ornek.com',
                'kind' => 'folder',
                'name' => 'D:\\web\\ornek.com',
                'reason' => 'klasor kilitli',
                'deliberate' => false,
            ),
        ),
        'completed' => false,
    ),
);
adapanel_recordLeftovers(ada_params(), $hata, array('userName' => 'musteri1'));
$not = (string) $GLOBALS['ada_test']['localApi'][0]['values']['notes'];
ada_assert(strpos($not, 'klasor kilitli') !== false, '409 zarfindaki kalinti da nota girdi');
ada_assert(strpos($not, 'Silinen siteler: eski.com') !== false, 'gercekten silinenler de yazili');

echo "6) Bozuk/eksik govde COKERTMEZ\n";
ada_sifirla();
adapanel_recordLeftovers(ada_params(), array(), array());
adapanel_recordLeftovers(ada_params(), array('data' => 'metin'), array());
adapanel_recordLeftovers(ada_params(), array('data' => array('leftovers' => array('bozuk'))), array());
ada_assert(count($GLOBALS['ada_test']['localApi']) === 1, 'yalniz gercekten kalinti tasiyan govde not uretti');

echo "7) Istisna, panelin GOVDESINI tasiyor (409 yolunda kalinti listesi kaybolmasin)\n";
$istisna = new AdaPanelApiException('mesaj', 'users.delete.incomplete', 409, $hata);
ada_assert($istisna->statusCode === 409, 'durum kodu tasiniyor');
ada_assert(isset($istisna->response['data']['leftovers']), 'yapisal kalinti listesi istisnada duruyor');

echo "8) Access Hash 'admin' alanini cozuyor (eski bicimler bozulmadan)\n";
$eski = AdaPanelClient::parseAccessHash('panel');
ada_assert($eski['entry'] === 'panel' && $eski['admin'] === '', 'sade bicim: yalniz giris yolu, admin bos');
$yeni = AdaPanelClient::parseAccessHash('entry=panel;insecure=1;token=adap_x;admin=whmcsadmin');
ada_assert($yeni['entry'] === 'panel', 'entry cozuldu');
ada_assert($yeni['insecure'] === true, 'insecure cozuldu');
ada_assert($yeni['token'] === 'adap_x', 'token cozuldu');
ada_assert($yeni['admin'] === 'whmcsadmin', 'admin cozuldu');

echo "\n";
if ($basarisiz > 0) {
    echo "SONUC: $basarisiz denetim KALDI\n";
    exit(1);
}

echo "SONUC: hepsi gecti\n";
exit(0);
