<template>
  <!--
    DOĞRULANAMAYAN KAYNAK SUNUCU — "yoksay"ı körlemesine tıklatmayan onay.

    NEDEN BÖYLE: göç edilen kaynak sunucular (Plesk 8443, WHM 2087, eski FTPS/SFTP) neredeyse her
    zaman kendinden imzalı sertifika taşır. Panel eskiden bu yüzden doğrulamayı TÜMDEN kapatıyordu;
    o istek ise kaynak sunucunun API anahtarını / FTP-SSH parolasını taşıyordu, yani araya giren
    biri sahte kimlikle bağlantıyı karşılayıp bunları okuyabilirdi.

    Doğrulamayı kayıtsız şartsız açmak da göçü kullanılamaz hâle getirirdi. Kopyalanan kalıp
    `ssh`ın host key onayıdır: kimlik REDDEDİLİR, ama reddedilirken NE görüldüğü — sebep, parmak
    izi, konu/veren, geçerlilik — yöneticiye GÖSTERİLİR. Yönetici parmak izini kaynak sunucudan
    (sağlayıcı konsolu, `ssh-keygen -l`, `openssl x509 -fingerprint -sha256`) teyit edip kabul
    eder; kabul YALNIZ o parmak izine yazılır.

    Parmak izi seçilebilir metindir: yönetici karşılaştıracaksa okuyabilmeli, kopyalayabilmeli.
  -->
  <div class="notice error trust-prompt" role="alert">
    <div class="trust-body">
      <strong>{{ t(`panel.migration.trust.${isHostKey ? "hostKeyTitle" : "title"}`) }}</strong>
      <span>{{ t(`panel.migration.trust.${isHostKey ? "hostKeyExplain" : "explain"}`) }}</span>

      <dl class="trust-facts">
        <div class="trust-fact">
          <dt>{{ t("panel.migration.trust.fingerprint") }}</dt>
          <dd><code class="trust-fingerprint">{{ prompt.fingerprint }}</code></dd>
        </div>
        <div v-if="prompt.reasons.length" class="trust-fact">
          <dt>{{ t("panel.migration.trust.reason") }}</dt>
          <dd>{{ reasonText }}</dd>
        </div>
        <div v-if="prompt.subject" class="trust-fact">
          <dt>{{ t("panel.migration.trust.subject") }}</dt>
          <dd>{{ prompt.subject }}</dd>
        </div>
        <div v-if="prompt.issuer" class="trust-fact">
          <dt>{{ t("panel.migration.trust.issuer") }}</dt>
          <dd>{{ prompt.issuer }}</dd>
        </div>
        <div v-if="prompt.validity" class="trust-fact">
          <dt>{{ t("panel.migration.trust.validity") }}</dt>
          <dd>{{ prompt.validity }}</dd>
        </div>
      </dl>

      <span class="meta">{{ t("panel.migration.trust.verifyHint") }}</span>
    </div>

    <button type="button" class="secondary compact" :disabled="busy" @click="emit('accept')">
      {{ t("panel.migration.trust.accept") }}
    </button>
  </div>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { useI18n } from "vue-i18n";
import type { RemoteTrustPrompt } from "../utils/remoteTrust";

const props = withDefaults(defineProps<{ prompt: RemoteTrustPrompt; busy?: boolean }>(), { busy: false });
const emit = defineEmits<{ (event: "accept"): void }>();

const { t } = useI18n();

// SSH'ta sertifika otoritesi yoktur → ilk bağlantıda onay HER ZAMAN gerekir; TLS'te ise yalnız
// zincir doğrulanamadığında. İki durumun metni farklı, çünkü yöneticinin sorusu farklı.
const isHostKey = computed(() => props.prompt.messageKey === "migration.hostkey.untrusted");

/*
 * SEBEP ÇEVRİLİR, HAM KOD BASILMAZ.
 * Arka uç makine okunur kodlar üretir (`self_signed`, `name_mismatch`, `expired`…). Ekranda ham
 * kod görünseydi yönetici "kendinden imzalı" ile "süresi geçmiş" arasındaki farkı — yani vereceği
 * kararı belirleyen tek şeyi — okuyamazdı. Bilinmeyen bir kod gelirse kodun kendisi gösterilir:
 * çeviri eksikliği, bilgiyi hiç göstermemek için gerekçe değildir.
 */
const reasonText = computed(() =>
  props.prompt.reasons
    .map((reason) => {
      const key = `panel.migration.trust.reasons.${reason}`;
      const translated = t(key);
      return translated === key ? reason : translated;
    })
    .join(", ")
);
</script>

<style scoped>
.trust-prompt {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.trust-body {
  display: grid;
  gap: 6px;
  min-width: 0;
}

.trust-facts {
  display: grid;
  gap: 3px;
  margin: 4px 0 0;
}

.trust-fact {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  min-width: 0;
}

.trust-fact dt {
  font-weight: 600;
}

.trust-fact dd {
  margin: 0;
  min-width: 0;
  word-break: break-all;
}

/* Parmak izi UZUN ve karakter karakter karşılaştırılır: sarmalanmalı ama bölünürken okunabilir
   kalmalı, bu yüzden tek boşluklu yazı tipi. */
.trust-fingerprint {
  font-family: var(--font-mono, ui-monospace, monospace);
  user-select: all;
  word-break: break-all;
}

.trust-prompt button {
  width: auto;
  flex: 0 0 auto;
}
</style>
