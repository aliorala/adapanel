<?php

/**
 * WHMCS'in module verdigi GLOBAL yuzey — testte birer casus olarak.
 *
 * NEDEN AYRI DOSYA: `logModuleCall`, `localAPI` ve `\WHMCS\Database\Capsule` WHMCS'in kendi
 * cekirdeginden gelir; bu depoda WHMCS YOK. Modul bunlari cagirdigi icin, bir kurulum olmadan
 * calistirilabilmesinin TEK yolu bu yuzeyi taklit etmektir. Taklit "her sey yolunda" demez:
 * her cagriyi KAYDEDER ki test "cagrildi mi, hangi degerlerle" sorusunu olcebilsin.
 *
 * `Capsule` ozellikle onemli: `adapanel_UsageUpdate` disk/trafik degerlerini dogrudan
 * `tblhosting` tablosuna yazar. Gercek WHMCS olmadan bu satirin CALISTIGINI, hangi SUTUNLARA
 * hangi degerleri yazdigini ve olculemeyen degerin yazilmadigini baska turlu gorulemez.
 */

namespace WHMCS\Database {

    /** `Capsule::raw('now()')` -- SQL ifadesini deger gibi tasiyan kucuk sarmalayici. */
    class RawIfade
    {
        public $ifade;

        public function __construct($ifade)
        {
            $this->ifade = (string) $ifade;
        }

        public function __toString()
        {
            return $this->ifade;
        }
    }

    /** Zincirlenen sorgu: ->where()->where()->update() cagrilarini kaydeder. */
    class SorguCasusu
    {
        private $tablo;
        private $kosullar = array();

        public function __construct($tablo)
        {
            $this->tablo = $tablo;
        }

        public function where($sutun, $deger)
        {
            $this->kosullar[$sutun] = $deger;

            return $this;
        }

        public function update(array $degerler)
        {
            if (!empty($GLOBALS['ada_test']['capsuleFirlat'])) {
                // Tek satirin yazilamamasi turu DURDURMAMALI; modulun bunu gercekten
                // yakaladigini olcmek icin kasitli patlatma yolu.
                throw new \Exception('veritabani yazilamadi (test)');
            }

            $GLOBALS['ada_test']['capsule'][] = array(
                'table' => $this->tablo,
                'where' => $this->kosullar,
                'values' => $degerler,
            );

            return 1;
        }
    }

    class Capsule
    {
        public static function table($tablo)
        {
            return new SorguCasusu($tablo);
        }

        public static function raw($ifade)
        {
            return new RawIfade($ifade);
        }
    }
}

namespace {

    if (!isset($GLOBALS['ada_test'])) {
        $GLOBALS['ada_test'] = array();
    }

    $GLOBALS['ada_test'] += array(
        'localApi' => array(),
        'moduleLog' => array(),
        'capsule' => array(),
        'capsuleFirlat' => false,
        'localApiResult' => array('result' => 'success', 'noteid' => 7),
    );

    if (!function_exists('localAPI')) {
        function localAPI($command, $values, $adminuser = '')
        {
            $GLOBALS['ada_test']['localApi'][] = array(
                'command' => $command,
                'values' => $values,
                'admin' => $adminuser,
            );

            return $GLOBALS['ada_test']['localApiResult'];
        }
    }

    if (!function_exists('logModuleCall')) {
        function logModuleCall($module, $action, $request, $response, $processed = '', $replace = array())
        {
            $GLOBALS['ada_test']['moduleLog'][] = array(
                'module' => $module,
                'action' => $action,
                'processed' => is_string($processed) ? $processed : json_encode($processed),
            );
        }
    }
}
