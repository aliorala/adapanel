<?php

/**
 * SAHTE ADAPANEL — WHMCS modulunun GERCEKTEN konustugu bir HTTP sunucusu.
 *
 * NEDEN VAR: `leftover-note.test.php` modulun WHMCS'e bagli olmayan KARAR noktalarini olcuyor
 * ama `AdaPanelClient`'in tek satirini calistirmiyor -- yani "modul panele hangi YOLA, hangi
 * METOTLA, hangi BASLIKLARLA, hangi ALAN ADLARIYLA gidiyor" sorusu bugune kadar hic olculmedi.
 * Bir istemciyi mock'layarak bunu olcemezsiniz: mock, istemcinin kendi kodunu atlar.
 *
 * Bu betik `php -S 127.0.0.1:<port> fake-panel.php` ile ayaga kalkar. Modul ona GERCEK curl
 * istegi atar; sunucu her istegi (metot + yol + sorgu + basliklar + ham govde) bir gunluge
 * yazar ve testin onceden yazdigi SENARYO'ya gore yanit doner. Boylece test, panelin C#
 * denetleyicileriyle karsilastirilabilecek somut bir kayit elde eder.
 *
 * Iki dosya sys_get_temp_dir() altinda paylasilir; test ve sunucu AYRI SURECLER oldugu icin
 * ortak durum ancak diskte olabilir:
 *   adapanel-fake-panel/requests.jsonl  -> sunucunun gordugu her istek (test bunu okur)
 *   adapanel-fake-panel/scenario.json   -> testin yazdigi yanit tarifi (sunucu bunu okur)
 *
 * Senaryo bicimi: { "GET /api/v1/users": {"status":200, "body":{...}}, "default": {...} }
 * `body` yerine `raw` verilirse govde OLDUGU GIBI (JSON olmayan yanitlari sinamak icin) doner.
 */

/** Test ile sunucunun bulusma noktasi; iki surec de AYNI yolu hesaplar. */
function ada_fake_dizin()
{
    $dizin = sys_get_temp_dir() . DIRECTORY_SEPARATOR . 'adapanel-fake-panel';
    if (!is_dir($dizin)) {
        @mkdir($dizin, 0777, true);
    }

    return $dizin;
}

// Bu dosya IKI ROLDE kullanilir: `php -S` altinda YONLENDIRICI, testin icinde ise yalnizca
// yukaridaki yol yardimcisini paylasan bir include. Ayrimi SAPI'den olculur; test bu dosyayi
// require ettiginde asagidaki istek isleme kodunun calismasi (ve $_SERVER'in yoklugunda
// patlamasi) gerekmez.
if (PHP_SAPI !== 'cli-server') {
    return;
}

$dizin = ada_fake_dizin();

$yol = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH);
$sorgu = (string) parse_url($_SERVER['REQUEST_URI'], PHP_URL_QUERY);
$metot = $_SERVER['REQUEST_METHOD'];
$govde = file_get_contents('php://input');

// BASLIKLAR OLCULUR: gizli giris yolu basligi, CSRF ve Authorization modulun en kolay
// unutacagi seyler ve unutuldugunda panel "yok" gibi davranir (404/400/401).
$basliklar = array();
foreach ($_SERVER as $anahtar => $deger) {
    if (strpos($anahtar, 'HTTP_') === 0) {
        $basliklar[strtolower(str_replace('_', '-', substr($anahtar, 5)))] = $deger;
    }
}
if (isset($_SERVER['CONTENT_TYPE'])) {
    $basliklar['content-type'] = $_SERVER['CONTENT_TYPE'];
}

file_put_contents(
    $dizin . DIRECTORY_SEPARATOR . 'requests.jsonl',
    json_encode(array(
        'method' => $metot,
        'path' => $yol,
        'query' => $sorgu,
        'headers' => $basliklar,
        'body' => $govde,
    )) . "\n",
    FILE_APPEND | LOCK_EX
);

$senaryo = array();
$senaryoDosyasi = $dizin . DIRECTORY_SEPARATOR . 'scenario.json';
if (is_file($senaryoDosyasi)) {
    $cozulmus = json_decode((string) file_get_contents($senaryoDosyasi), true);
    if (is_array($cozulmus)) {
        $senaryo = $cozulmus;
    }
}

// Eslesme sirasi: once tam "METOT /yol", sonra yalniz yol, sonra "default".
// Sabit kimlikler (GUID) yollarda gectigi icin joker (*) desteklenir.
$tarif = ada_fake_tarif_bul($senaryo, $metot, $yol);

if ($tarif === null) {
    // Tarifi olmayan uc SESSIZCE basarili donmez: test, kendisinin beklemedigi bir cagriyi
    // fark etmeli. 599 + JSON olmayan govde, modulun hata yolunu da uyandirir.
    http_response_code(599);
    header('Content-Type: text/plain');
    echo 'senaryoda tarif yok: ' . $metot . ' ' . $yol;
    return;
}

http_response_code(isset($tarif['status']) ? (int) $tarif['status'] : 200);

if (array_key_exists('raw', $tarif)) {
    header('Content-Type: ' . (isset($tarif['contentType']) ? $tarif['contentType'] : 'text/html'));
    echo (string) $tarif['raw'];
    return;
}

header('Content-Type: application/json');
echo json_encode(isset($tarif['body']) ? $tarif['body'] : array('success' => true, 'data' => null));

function ada_fake_tarif_bul(array $senaryo, $metot, $yol)
{
    if (isset($senaryo[$metot . ' ' . $yol])) {
        return $senaryo[$metot . ' ' . $yol];
    }

    foreach ($senaryo as $desen => $tarif) {
        if (strpos($desen, '*') === false) {
            continue;
        }

        $duzenli = '#^' . str_replace('\*', '[^ ]*', preg_quote($desen, '#')) . '$#';
        if (preg_match($duzenli, $metot . ' ' . $yol)) {
            return $tarif;
        }
    }

    if (isset($senaryo[$yol])) {
        return $senaryo[$yol];
    }

    return isset($senaryo['default']) ? $senaryo['default'] : null;
}
