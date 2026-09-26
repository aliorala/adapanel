<template>
  <section class="panel module-shell">
    <!-- ÜÇ KROM SATIRI İKİYE İNDİ. SAYILDI: içerikten önce üst üste üç şerit vardı — başlık +
         "Yenile" düğmesi, sonra sekmeler + site seçici, sonra bölümün kendi başlığı + eylemleri.
         "Yenile" en üstteydi ama tazelediği şey (sekmenin listesi ve seçili sitenin durumu) iki
         satır aşağıdaydı; üstelik seçicinin YANINDA olması gereken bir eylem, sayfanın öbür
         ucundaydı. Düğme, üzerinde çalıştığı seçicinin yanına taşındı ve başlık şeridi yalnız
         başlığı taşıyor. -->
    <div class="module-head">
      <div>
        <h2>{{ t("panel.security.workspaceTitle") }}</h2>
        <p class="meta">{{ t("panel.security.subtitle") }}</p>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div class="module-toolbar">
      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="activeSection === 'site'" :class="{ active: activeSection === 'site' }" @click="activeSection = 'site'">{{ t("panel.security.siteTitle") }}</button>
        <button role="tab" :aria-selected="activeSection === 'firewall'" :class="{ active: activeSection === 'firewall' }" @click="activeSection = 'firewall'">{{ t("panel.firewall.title") }}</button>
        <button role="tab" :aria-selected="activeSection === 'bruteforce'" :class="{ active: activeSection === 'bruteforce' }" @click="activeSection = 'bruteforce'">{{ t("panel.bruteforce.title") }}</button>
        <button role="tab" :aria-selected="activeSection === 'antivirus'" :class="{ active: activeSection === 'antivirus' }" @click="activeSection = 'antivirus'">{{ t("panel.antivirus.title") }}</button>
      </div>
      <!-- `site-select` yerine ORTAK `compact-select` (base.css): yerel kopya `min(280px, 52vw)`
           ile SABIT bir genislik dayatiyordu — kisa bir site adinda kutu bos yere 280px yer
           kapliyor, uzun bir adda 52vw'de kirpiliyordu. Ortak tanim icerige uyar (`width: auto`,
           taban 140px) ve panelin diger yedi site secicisiyle AYNI davranir. -->
      <div class="toolbar-group">
        <select v-model="selectedSiteName" :aria-label="t('panel.php.selectSite')" class="compact-select">
          <option value="">{{ t("panel.php.selectSite") }}</option>
          <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
        </select>
        <button class="secondary compact" :disabled="!selectedSiteName" @click="refreshAll">
          {{ t("panel.security.refresh") }}
        </button>
      </div>
    </div>

    <template v-if="activeSection === 'site'">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.security.siteTitle") }}</h3>
          <p class="meta">{{ selectedSiteName || t("panel.sites.detailEmpty") }}</p>
        </div>
        <div class="row-actions">
          <button class="secondary compact" :disabled="!selectedSiteName" @click="showSiteForm = !showSiteForm">
            {{ showSiteForm ? t("panel.common.cancel") : t("panel.security.advancedSettings") }}
          </button>
          <button class="compact" :disabled="!selectedSiteName || busyAction === 'rules'" @click="runSiteAction('rules')">
            {{ busyAction === "rules" ? t("panel.security.busy") : t("panel.security.basicRules") }}
          </button>
        </div>
      </div>

      <!-- SİTE SEÇİLMEMİŞKEN EKRAN BOMBOŞTU. Üç durumdan yalnız ikisi çiziliyordu ("yükleniyor"
           ve "dolu"); site seçilmemişse başlık satırı ile form arasında HİÇBİR ŞEY yoktu ve
           kullanıcı dört düğmenin neden pasif olduğunu göremiyordu. Boşluk bir cevap değildir:
           eksik olan şey ADIYLA söyleniyor ve seçici hemen yukarıda duruyor. -->
      <p v-if="!selectedSiteName" class="empty-state">{{ t("panel.php.selectSite") }}</p>

      <!-- Site güvenlik durumu okunmadan izolasyon/ACL/kural rozetleri "kapalı" görünüyordu;
           korumalı bir site korumasız sanılabiliyordu. -->
      <p v-else-if="!securityConfig" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>

      <!-- `security-summary` KALDIRILDI: kural birebir base.css'teki `.summary-strip` idi
           (`repeat(4, minmax(0,1fr))`) ve alttaki 900px kirilimi da base'dekinin AYNISIYDI.
           Iki kopya ayni seyi soyluyordu; tek kaynak birakildi. -->
      <!-- ÖLÇÜLEMEYEN DEĞER ARTIK "AÇIK"/"KAPALI" DEMİYOR.
           ÖLÇÜLDÜ (kurulu panel, `GET /sites/alioral.xyz/security` yanıtı): sunucu dört
           `*Measured` bayrağını da gönderiyor, arayüz hiçbirini okumuyordu. Ölçüm başarısızsa
           sunucu son bilinen KAYDI döndürür — ekran ise onu ölçülmüş gibi yeşil "Açık" rozetiyle
           çiziyordu. Bu ekranda o hata, korunmayan bir siteye "korunuyor" demek anlamına gelir;
           bu depodaki en pahalı hata sınıfı budur.
           ÜÇ DURUM, ÜÇ ROZET: açık / kapalı / ölçülemedi. Ölçülemeyeni "kapalı" göstermek de
           yanlış olurdu (geçici arızayı kalıcı yanlış duruma çevirir) — nitekim arka uçtaki
           DTO'nun kendi açıklaması bu bayrağı tam olarak bunun için taşıyor. -->
      <div v-else-if="securityConfig" class="summary-strip">
        <div class="summary-item">
          <span>{{ t("panel.security.isolationStatus") }}</span>
          <strong :class="['status-pill', stateTone(securityConfig.isIsolated, securityConfig.isIsolatedMeasured)]">
            {{ stateLabel(securityConfig.isIsolated, securityConfig.isIsolatedMeasured) }}
          </strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.security.aclStatus") }}</span>
          <strong :class="['status-pill', stateTone(securityConfig.folderAclApplied, securityConfig.folderAclMeasured)]">
            {{ stateLabel(securityConfig.folderAclApplied, securityConfig.folderAclMeasured) }}
          </strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.security.rulesStatus") }}</span>
          <strong :class="['status-pill', stateTone(securityConfig.basicRulesEnabled, securityConfig.basicRulesMeasured)]">
            {{ stateLabel(securityConfig.basicRulesEnabled, securityConfig.basicRulesMeasured) }}
          </strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.security.requestsPerMinute") }}</span>
          <!-- "Sınırsız" YALNIZ ÖLÇÜLDÜYSE yazılır: DTO'da 0 = sınır yok demek, ama web.config
               okunamadığında da 0 gelir. İki hâli aynı kelimeyle anlatmak, sınırı olmayan bir
               siteyi değil, sınırı BİLİNMEYEN bir siteyi "sınırsız" ilan etmekti. -->
          <strong v-if="securityConfig.rateLimitMeasured === false" class="status-pill warn">
            {{ t("panel.security.unmeasured") }}
          </strong>
          <strong v-else>{{ securityConfig.requestsPerMinute || t("panel.security.unlimited") }}</strong>
        </div>
      </div>

      <!-- Rozet tek başına "neden" demiyor; açıklama şeridin ALTINDA, yalnızca gerçekten
           ölçülemeyen bir satır varken çıkar. Her açılışta duran bir uyarı okunmaz olur. -->
      <p v-if="securityConfig && hasUnmeasured" class="field-hint">{{ t("panel.security.unmeasuredHint") }}</p>

      <div v-if="showSiteForm" class="form-surface">
        <div class="form-grid">
          <label>
            <span class="field-label">{{ t("panel.security.principal") }}</span>
            <!-- Yer tutucu FilesView'daki AYNI alandan geliyor: kutunun ne beklediğini boş
                 bir kutu söylemiyordu, "IIS_IUSRS" biçimi tek bakışta anlatıyor. -->
            <input v-model="aclForm.principal" placeholder="IIS_IUSRS" spellcheck="false" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.security.rights") }}</span>
            <!-- HAM `icacls` KODU EKRANDAN KALKTI. Alan serbest metin kutusuydu ve içinde tek
                 başına "M" yazıyordu; bu, kullanıcının icacls'in harf kodlarını EZBERE bilmesini
                 şart koşuyor, üstelik yanlış yazılan bir harf sunucuya hata olarak gidiyordu.
                 Panelin AYNI ayarı FilesView'da zaten çevrilmiş bir listeyle soruyordu ve
                 `panel.security.rights.read/modify/full` anahtarları oradan beri VAR — yani
                 burada çeviri eksik değildi, kullanılmıyordu. Gönderilen değer aynı: R / M / F. -->
            <select v-model="aclForm.rights">
              <option value="R">{{ t("panel.security.rights.read") }}</option>
              <option value="M">{{ t("panel.security.rights.modify") }}</option>
              <option value="F">{{ t("panel.security.rights.full") }}</option>
            </select>
          </label>
          <label>
            <span class="field-label">{{ t("panel.security.requestsPerMinute") }}</span>
            <input v-model.number="rateForm.requestsPerMinute" type="number" min="1" />
          </label>
        </div>
        <!-- UCU DE IKINCIL. Bu uc dugme UC AYRI ayari uygular (izolasyon, klasor ACL, hiz
             siniri) ve aralarinda bir sira ya da onem farki YOK. Once yalnizca "Izole et"
             birincil maviydi; esit rutbedeki uc eylemden birini vurgulamak, goze var olmayan
             bir hiyerarsiyi anlatiyordu. Bolumun tek birincil eylemi baslik satirindaki
             "Temel kurallar" olarak kaldi. -->
        <div class="row-actions">
          <button class="secondary compact" :disabled="!selectedSiteName || busyAction === 'isolate'" @click="runSiteAction('isolate')">
            {{ busyAction === "isolate" ? t("panel.security.busy") : t("panel.security.isolate") }}
          </button>
          <button class="secondary compact" :disabled="!selectedSiteName || busyAction === 'acl'" @click="runSiteAction('acl')">
            {{ busyAction === "acl" ? t("panel.security.busy") : t("panel.security.applyAcl") }}
          </button>
          <button class="secondary compact" :disabled="!selectedSiteName || busyAction === 'rate'" @click="runSiteAction('rate')">
            {{ busyAction === "rate" ? t("panel.security.busy") : t("panel.security.saveRate") }}
          </button>
        </div>
      </div>
    </template>

    <template v-else-if="activeSection === 'firewall'">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.firewall.title") }}</h3>
          <p class="meta">{{ t("panel.firewall.subtitle") }}</p>
        </div>
        <!-- Form ACIKKEN bu dugme "Iptal"e donusuyor ama SINIFI degismiyordu: panelin en baskin
             ogesi (birincil mavi) bir VAZGECME eylemiydi ve hemen altindaki gercek eylem
             ("Izin ver") onunla ayni agirliktaydi. Acikken ikincile duser; boylece formun
             gonder dugmesi sekmenin TEK birincili olur. -->
        <button :class="showFirewallForm ? 'secondary compact' : 'compact'" @click="showFirewallForm = !showFirewallForm">
          {{ showFirewallForm ? t("panel.common.cancel") : t("panel.firewall.newRule") }}
        </button>
      </div>

      <div v-if="showFirewallForm" class="form-surface">
        <div class="form-grid">
          <label>
            <span class="field-label">{{ t("panel.firewall.ruleName") }}</span>
            <input v-model="firewallForm.ruleName" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.sites.port") }}</span>
            <input v-model.number="firewallForm.port" type="number" min="1" max="65535" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.firewall.protocol") }}</span>
            <select v-model="firewallForm.protocol">
              <option value="TCP">TCP</option>
              <option value="UDP">UDP</option>
            </select>
          </label>
        </div>
        <!-- "ENGELLE" AYRILDI. Kod yorumunda zaten olculmus bir tehlike var (bkz. `firewallForm`
             varsayilani): iki dugme yan yana ve AYNI kutu boyundayken yanlis olana tek tik,
             sunucuyu disariya kapatmaya yetiyordu. Simdi hem RENK hem KONUM ayiriyor: engelleme
             tehlike tonunda ve `margin-left: auto` ile satirin obur ucunda, sabit yerde durur.
             Metin de tek basina yeterli degildi — iki etiket de ayni uzunlukta ve ayni gride. -->
        <div class="row-actions">
          <button class="compact" :disabled="busyAction === 'allow'" @click="saveFirewall(true)">
            {{ busyAction === "allow" ? t("panel.security.busy") : t("panel.firewall.allow") }}
          </button>
          <button class="danger compact destructive-slot" :disabled="busyAction === 'block'" @click="saveFirewall(false)">
            {{ busyAction === "block" ? t("panel.security.busy") : t("panel.firewall.block") }}
          </button>
        </div>
      </div>

      <div class="data-table">
        <div class="data-row table-head firewall-row">
          <span>{{ t("panel.firewall.ruleName") }}</span>
          <span>{{ t("panel.firewall.protocol") }}</span>
          <span>{{ t("panel.sites.port") }}</span>
          <span>{{ t("panel.sites.state") }}</span>
          <span>{{ t("panel.files.actions") }}</span>
        </div>
        <div v-for="rule in firewallRules" :key="rule.displayName" class="data-row firewall-row">
          <strong>{{ rule.displayName }}</strong>
          <span>{{ rule.protocol }}</span>
          <span>{{ rule.localPort }}</span>
          <!-- Rozetin SÖZCÜĞÜ artık çeviriden geliyor. Öncesi: Windows'un döndürdüğü ham değer
               ("Allow" / "Block") basılıyordu — panelin Türkçesinde İngilizce, üstelik hemen
               üstteki formda AYNI iki eylemin düğmeleri "İzin ver" / "Engelle" yazıyordu. Aynı
               kavram tek ekranda iki dilde görünüyordu.
               TANINMAYAN DEĞER ÇEVRİLMEZ, HAM basılır (bkz. `utils/firewallAction.ts`): Windows
               başka bir eylem döndürürse ona "Engelle" demek, ölçülmemiş bir iddia olurdu.
               RENK DE ÜÇE ÇIKTI: eskiden "allow değilse warn" idi, yani TANINMAYAN bir eylem
               "engelleniyor" tonunda çiziliyordu — sözcük dürüstken renk uyduruyordu. -->
          <span :class="['status-pill', actionTone(rule.action)]">{{ actionLabel(rule.action) }}</span>
          <!-- Panelin kendi kuralları GERİ ALINABİLİR olmalı: kural yazıp silememek tek yönlü bir
               kapıydı, yanlışlıkla eklenen bir engelleme panelden kaldırılamıyordu. -->
          <span class="row-actions">
            <button
              v-if="rule.displayName.startsWith('AdaPanel ')"
              class="danger compact"
              :disabled="busyAction === `remove:${rule.displayName}`"
              @click="removeRule(rule.displayName)">
              {{ busyAction === `remove:${rule.displayName}` ? t("panel.security.busy") : t("panel.sites.delete") }}
            </button>
          </span>
        </div>
        <p v-if="!firewallLoaded" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
        <p v-else-if="firewallRules.length === 0" class="empty-state">{{ t("panel.firewall.empty") }}</p>
      </div>
    </template>

    <template v-else-if="activeSection === 'antivirus'">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.antivirus.title") }}</h3>
          <p class="meta">{{ t("panel.antivirus.subtitle") }}</p>
        </div>
        <span v-if="avStatus" :class="['status-pill', avStatus.isInstalled ? 'ok' : 'warn']">
          {{ avStatus.isInstalled
            ? t("panel.antivirus.installedVersion", { version: avStatus.version })
            : t("panel.antivirus.notInstalled") }}
        </span>
      </div>

      <!-- Kurulum / tanım güncelleme / tarama SAATLERCE sürebilir; şerit ne sürdüğünü ve geçen
           süreyi söyler. Şeridin yeri bilinçli olarak kartların ÜSTÜ: hangi düğmeye basılmış
           olursa olsun aynı yerde görünür.
           Şerit İKİ kaynaktan beslenir: bu sekmede başlatılan iş (useRunningTask) ve SUNUCUDA
           süren tarama (/antivirus/status). İkincisi sayfa yenilendikten sonra da doludur. -->
      <RunningTaskBanner
        :running="avBannerRunning"
        :label="avBannerLabel"
        :elapsed-text="avBannerElapsedText"
        :hint="avBannerHint" />

      <!-- Durum gelmeden "kurulu değil" + kurulum butonu gösteriliyordu; kurulu ClamAV
           yeniden kurulmaya çalışılabiliyordu. -->
      <p v-if="!avStatus" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>

      <div v-else-if="!avStatus.isInstalled" class="form-surface">
        <p class="meta">{{ t("panel.antivirus.installHint") }}</p>
        <div class="row-actions">
          <button class="compact" :disabled="busyAction === 'av'" @click="installAv">
            {{ busyAction === "av" ? t("panel.antivirus.installing") : t("panel.antivirus.install") }}
          </button>
        </div>
      </div>

      <template v-else>
        <div class="form-surface">
          <div class="section-head">
            <div>
              <h4>{{ t("panel.antivirus.defsTitle") }}</h4>
              <p class="meta">
                {{ avStatus.databaseReady && avStatus.databaseUpdatedAtUtc
                  ? t("panel.antivirus.defsUpdatedAt", { date: new Date(avStatus.databaseUpdatedAtUtc).toLocaleString() })
                  : t("panel.antivirus.defsMissing") }}
              </p>
            </div>
            <button class="secondary compact" :disabled="busyAction === 'av'" @click="updateAvDefs">
              {{ busyAction === "av" ? t("panel.antivirus.updating") : t("panel.antivirus.updateDefs") }}
            </button>
          </div>
          <p v-if="!avStatus.databaseReady" class="field-hint">{{ t("panel.antivirus.defsHint") }}</p>
        </div>

        <div class="form-surface">
          <h4>{{ t("panel.antivirus.scanTitle") }}</h4>
          <div class="form-grid">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.php.targetSite") }}</span>
              <select v-model="avSiteName">
                <option value="">{{ t("panel.php.selectSite") }}</option>
                <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
              </select>
            </label>
          </div>
          <p class="field-hint">{{ t("panel.antivirus.scanHint") }}</p>
          <!-- Sunucuda zaten bir tarama sürerken ikinci taramanın reddedileceğini (409) düğmeye
               basmadan ÖNCE söyler; kullanıcı hatayla karşılaşmasın. -->
          <p v-if="avServerScan" class="field-hint">
            {{ t("panel.antivirus.scanBusyHint", { target: avServerScan.target }) }}
          </p>
          <div class="row-actions">
            <button
              class="compact"
              :disabled="busyAction === 'av' || avServerScan !== null || !avSiteName || !avStatus.databaseReady"
              @click="runAvScan">
              {{ busyAction === "av" || avServerScan ? t("panel.antivirus.scanning") : t("panel.antivirus.scan") }}
            </button>
          </div>
        </div>

        <div v-if="avStatus.lastScan" class="form-surface">
          <div class="section-head">
            <div>
              <h4>{{ t("panel.antivirus.lastScanTitle") }}: {{ avStatus.lastScan.target }}</h4>
              <!-- Yarıda kalan taramada "0 dosya tarandı, 0 sn sürdü" YAZILMAZ: o sayılar hiç
                   ölçülmedi, yazmak taramanın bittiğini ve boş çıktığını sandırırdı. -->
              <p class="meta">
                {{ new Date(avStatus.lastScan.startedAtUtc).toLocaleString() }}
                <template v-if="avStatus.lastScan.status !== 'interrupted'">
                  • {{ t("panel.antivirus.scanSummary", {
                    files: avStatus.lastScan.scannedFiles,
                    seconds: avStatus.lastScan.durationSeconds
                  }) }}
                </template>
              </p>
            </div>
            <!-- ÜÇ SONUÇ, ÜÇ TON. Öncesi ikiliydi: "temiz" yeşil, DİĞER HER ŞEY kehribar — yani
                 virüs BULUNMUŞ bir tarama ile yarıda kalmış bir tarama aynı renkteydi. Oysa
                 biri "sunucunda zararlı dosya var", diğeri "ölçüm tamamlanmadı". Rozetin metni
                 zaten üçünü ayırıyordu; eksik olan renk kanalının aynı şeyi söylemesiydi. -->
            <span :class="['status-pill', scanTone(avStatus.lastScan.status)]">
              {{ t(`panel.antivirus.status.${avStatus.lastScan.status}`) }}
            </span>
          </div>
          <p v-if="avStatus.lastScan.status === 'failed'" class="notice error" role="alert">{{ avStatus.lastScan.detail }}</p>
          <p v-else-if="avStatus.lastScan.status === 'interrupted'" class="notice error" role="alert">
            {{ t("panel.antivirus.interruptedHint") }}
          </p>
          <div v-else-if="avStatus.lastScan.infected.length > 0" class="data-table">
            <div class="data-row table-head av-finding-row">
              <span>{{ t("panel.antivirus.file") }}</span>
              <span>{{ t("panel.antivirus.signature") }}</span>
            </div>
            <div v-for="finding in avStatus.lastScan.infected" :key="finding.path" class="data-row av-finding-row">
              <span class="cell-muted av-path">{{ finding.path }}</span>
              <strong>{{ finding.signature }}</strong>
            </div>
          </div>
          <p v-else class="empty-state">{{ t("panel.antivirus.noFindings") }}</p>
        </div>
      </template>
    </template>

    <template v-else>
      <div class="section-head">
        <div>
          <h3>{{ t("panel.bruteforce.title") }}</h3>
          <p class="meta">{{ t("panel.bruteforce.subtitle") }}</p>
        </div>
        <label v-if="bruteForceLoaded" class="toggle-field">
          <input v-model="bruteForm.enabled" type="checkbox" />
          <span>{{ t("panel.bruteforce.enabled") }}</span>
        </label>
      </div>

      <!-- Kaba kuvvet ayarları gelmeden anahtar ve eşikler varsayılan değerlerini gösteriyordu. -->
      <p v-if="!bruteForceLoaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>

      <div v-else class="form-surface">
        <div class="form-grid">
          <label>
            <span class="field-label">{{ t("panel.bruteforce.maxAttempts") }}</span>
            <input v-model.number="bruteForm.maxFailedAttempts" type="number" min="1" max="1000" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.bruteforce.windowMinutes") }}</span>
            <input v-model.number="bruteForm.windowMinutes" type="number" min="1" max="1440" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.bruteforce.banMinutes") }}</span>
            <input v-model.number="bruteForm.banMinutes" type="number" min="0" max="525600" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.bruteforce.whitelist") }}</span>
            <input v-model="bruteWhitelistText" placeholder="203.0.113.5, 198.51.100.7" />
          </label>
        </div>
        <div class="row-actions">
          <button class="compact" :disabled="busyAction === 'bruteforce'" @click="saveBruteForce">
            {{ busyAction === "bruteforce" ? t("panel.security.busy") : t("panel.common.save") }}
          </button>
        </div>
      </div>

      <div class="data-table">
        <div class="data-row table-head bruteforce-row">
          <span>{{ t("panel.bruteforce.ip") }}</span>
          <span>{{ t("panel.bruteforce.attempts") }}</span>
          <span>{{ t("panel.bruteforce.bannedAt") }}</span>
          <span>{{ t("panel.bruteforce.expiresAt") }}</span>
          <span>{{ t("panel.software.action") }}</span>
        </div>
        <div v-for="ban in bannedIps" :key="ban.ipAddress" class="data-row bruteforce-row">
          <strong>{{ ban.ipAddress }}</strong>
          <span>{{ ban.failedAttempts }}</span>
          <span class="cell-muted">{{ new Date(ban.bannedAtUtc).toLocaleString() }}</span>
          <span class="cell-muted">{{ ban.expiresAtUtc ? new Date(ban.expiresAtUtc).toLocaleString() : t("panel.bruteforce.permanent") }}</span>
          <button class="secondary compact row-button" :disabled="busyAction === 'unban'" @click="unban(ban.ipAddress)">
            {{ t("panel.bruteforce.unban") }}
          </button>
        </div>
        <!-- Yasaklı IP listesi kaba kuvvet durumuyla BİRLİKTE gelir; bekleme/hata ayrımı için
             zaten var olan `bruteForceLoaded` kullanılır, ayrı bir sayaç uydurulmaz. -->
        <p v-if="!bruteForceLoaded" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
        <p v-else-if="bannedIps.length === 0" class="empty-state">{{ t("panel.bruteforce.empty") }}</p>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { allowPort, blockPort, getFirewallRules, removeFirewallRule, type FirewallRule } from "../api/firewall";
import { useConfirm } from "../components/ConfirmDialog.vue";
import RunningTaskBanner from "../components/RunningTaskBanner.vue";
import { useRunningTask } from "../composables/useRunningTask";
import {
  applyAcl,
  enableBasicRules,
  getBruteForce,
  getSiteSecurity,
  isolateSite,
  setRateLimit,
  unbanIp,
  updateBruteForce,
  type BannedIp,
  type BruteForceSettings,
  type SiteSecurityConfiguration
} from "../api/security";
import {
  getAntivirusStatus,
  installAntivirus,
  scanAntivirus,
  updateAntivirusDefinitions,
  type AntivirusStatus
} from "../api/antivirus";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
// Eylem sözcüğü İKİ ekranda çiziliyor; kural tek kaynakta (bkz. utils/firewallAction.ts).
import { firewallActionLabel, normalizeFirewallAction } from "../utils/firewallAction";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const securityConfig = ref<SiteSecurityConfiguration | null>(null);
const firewallRules = ref<FirewallRule[]>([]);
// Kural listesi BİR KEZ başarıyla okundu mu. `loading` hata durumunda da false olur; boş dizi
// "kural yok" diye okunuyordu — güvenlik ekranında bu, korumasız olduğunu sanmak demek.
const firewallLoaded = ref(false);
const notice = ref("");
const noticeError = ref(false);
const busyAction = ref("");
const loading = ref(false);
const activeSection = ref<"site" | "firewall" | "bruteforce" | "antivirus">("site");
// ClamAV antivirüs (Güvenlik > Antivirüs). Durum sekme ilk açıldığında yüklenir.
const avStatus = ref<AntivirusStatus | null>(null);
// Kurulum / tanım güncelleme / tarama DAKİKALAR–SAATLER sürer; hepsi aynı şeridi kullanır
// (aynı anda yalnız biri çalışabilir: üçü de `busyAction === "av"` ile kilitli).
const avTask = useRunningTask();
// İpucu işe göre değişir: tarama saatler sürebilir, tanım indirmesi ~250 MB'dır. Genel
// "sayfadan ayrılmayın" metni ikisini de açıklamıyordu.
const avScanRunningHint = computed(() =>
  avTask.label.value === t("panel.antivirus.runningDefs")
    ? t("panel.antivirus.runningDefsHint")
    : t("panel.antivirus.runningHint"));
const avSiteName = ref("");

/*
 * SAYFA YENİLENDİĞİNDE "TARAMA SÜRÜYOR" ARTIK KAYBOLMUYOR.
 *
 * ÖNCESİ: durum yalnız bu sekmenin belleğindeydi. Kullanıcı 2 saatlik taramayı başlatıp sayfayı
 * yenilediğinde panel "tarama yok" diyordu — geri okunacak bir şey yoktu, çünkü `/antivirus/status`
 * yalnız SON BİTEN taramayı döndürüyordu.
 *
 * ŞİMDİ: sunucu tarama süresince `runningScan` (hedef + başlangıç anı) döndürüyor; sekme açılır
 * açılmaz okunuyor ve şerit oradan besleniyor.
 *
 * SÜRE NEDEN DAKİKA CİNSİNDEN: geçen süre SUNUCUNUN verdiği başlangıç anından hesaplanıyor.
 * `useRunningTask` süreyi kendi `begin()` anından ölçer ve dışarıdan başlangıç almaz (o dosya bu
 * görevin kapsamında değil), bu yüzden yenilenmiş sayfada onu kullanmak "0:05" yazıp 40 dakikadır
 * süren bir taramayı YENİ BAŞLAMIŞ göstermek olurdu. Saniye göstermek için ayrı bir saniyelik
 * zamanlayıcı eklemek yerine, ZATEN VAR OLAN yoklama döngüsü saat işlevi görüyor: 125 dakikalık
 * bir işte dakika çözünürlüğü dürüst ve yeterli.
 */
const avServerScan = computed(() => avStatus.value?.runningScan ?? null);
// Yoklama her tamamlandığında tazelenir; ekrandaki geçen süre bundan hesaplanır.
const avClockMs = ref(Date.now());
let avPollTimer: ReturnType<typeof setTimeout> | undefined;

const avServerElapsedText = computed(() => {
  const scan = avServerScan.value;
  if (!scan) return "";
  const startedAt = new Date(scan.startedAtUtc).getTime();
  if (Number.isNaN(startedAt)) return "";
  const minutes = Math.max(0, Math.floor((avClockMs.value - startedAt) / 60_000));
  return minutes < 60
    ? t("panel.antivirus.elapsedMinutes", { minutes })
    : t("panel.antivirus.elapsedHours", { hours: Math.floor(minutes / 60), minutes: minutes % 60 });
});

// Bu sekmede başlatılan iş varsa ONUN saniyelik sayacı gösterilir (daha hassastır); yoksa
// sunucudan okunan tarama.
const avBannerRunning = computed(() => avTask.running.value || avServerScan.value !== null);
const avBannerLabel = computed(() =>
  avTask.running.value
    ? avTask.label.value
    : t("panel.antivirus.runningScan", { site: avServerScan.value?.target ?? "" }));
const avBannerElapsedText = computed(() =>
  avTask.running.value ? avTask.elapsedText.value : avServerElapsedText.value);
const avBannerHint = computed(() =>
  avTask.running.value ? avScanRunningHint.value : t("panel.antivirus.resumedHint"));
const showFirewallForm = ref(false);
const showSiteForm = ref(false);
const aclForm = reactive({ principal: "", rights: "M" });
const rateForm = reactive({ requestsPerMinute: 120 });
// Varsayılan port BOŞ. Eskiden 8890 (panelin KENDİ portu) yazılı geliyordu ve "İzin ver" ile
// "Engelle" düğmeleri yan yana duruyordu: yanlış düğmeye tek tık, paneli dışarıya kapatmaya
// yetiyordu. Sunucu artık bunu reddediyor, ama tehlikeli bir değeri hazır sunmanın da gereği yok.
const firewallForm = reactive({ ruleName: "", port: 0, protocol: "TCP" });
const bannedIps = ref<BannedIp[]>([]);
// Ayarlar okunmadan önceki varsayılanlar gerçek yapılandırma sanılıyordu.
const bruteForceLoaded = ref(false);
const bruteWhitelistText = ref("");
const bruteForm = reactive<BruteForceSettings>({
  enabled: true,
  maxFailedAttempts: 10,
  windowMinutes: 15,
  banMinutes: 60,
  whitelistedIps: []
});

const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});

onMounted(async () => {
  try {
    if (sitesStore.sites.length === 0) await sitesStore.fetchSites();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
  await refreshAll();
});

watch(selectedSiteName, refreshAll);

watch(activeSection, async (section) => {
  if (section === "antivirus" && avStatus.value === null) {
    await refreshAvStatus();
  }
});

// Yoklamanın açılıp kapanması TEK yerden yönetilir: sunucuda tarama belirdiğinde başlar, tarama
// bittiğinde ya da işi bu sekme yürütmeye başladığında durur. Çağrıyı her fonksiyona elle
// serpiştirmek, bir yolu unutup zamanlayıcıyı sonsuza kadar açık bırakma riski taşırdı.
watch([avServerScan, avTask.running], avSyncPolling, { immediate: true });

async function refreshAvStatus() {
  try {
    avStatus.value = await getAntivirusStatus();
    avClockMs.value = Date.now();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

/**
 * Sunucuda tarama sürerken durumu yoklar.
 *
 * YALNIZCA sunucuda süren ve BU SEKMEDE başlatılMAMIŞ tarama için çalışır: bu sekme taramayı
 * kendisi başlattıysa sonucu zaten açık duran istekten alacak, yoklamaya gerek yok.
 *
 * 15 saniye: 125 dakikaya kadar süren bir iş için bu sıklık ekranı canlı tutar ve sunucuya yük
 * bindirmez. Sabit aralık yerine "bir tur bitince bir sonrakini kur" kalıbı kullanılıyor ki yavaş
 * bir yanıt istekleri ÜST ÜSTE bindirmesin.
 */
function avStopPolling() {
  if (avPollTimer !== undefined) {
    clearTimeout(avPollTimer);
    avPollTimer = undefined;
  }
}

function avSyncPolling() {
  if (avServerScan.value !== null && !avTask.running.value) {
    if (avPollTimer === undefined) {
      avPollTimer = setTimeout(avPollTick, 15_000);
    }
    return;
  }

  avStopPolling();
}

async function avPollTick() {
  avPollTimer = undefined;
  const oncedenSuruyordu = avServerScan.value !== null;
  await refreshAvStatus();

  if (avServerScan.value !== null) {
    avSyncPolling();
    return;
  }

  // Tarama BİTTİ. Sonucu bu sekme istemedi (sayfa yenilenmişti), o yüzden sonucu burada duyurmak
  // gerekiyor; aksi halde şerit sessizce kaybolur ve kullanıcı ne olduğunu anlamaz.
  if (oncedenSuruyordu && !avTask.running.value) {
    const son = avStatus.value?.lastScan ?? null;
    if (son?.status === "infected") {
      showNotice(t("panel.antivirus.scanInfected", { count: son.infectedCount }), true);
    } else if (son?.status === "clean") {
      showNotice(t("panel.antivirus.scanClean"), false);
    } else if (son?.status === "interrupted") {
      showNotice(t("panel.antivirus.interruptedHint"), true);
    }
  }
}

// Bileşen yok olduktan sonra yoklamaya devam eden bir zamanlayıcı sızıntıdır.
onUnmounted(avStopPolling);

/*
 * ANTİVİRÜS İŞLERİ "SÜRÜYOR" ŞERİDİNE BAĞLANDI (composables/useRunningTask.ts).
 *
 * NEDEN: tarama isteğinin zaman aşımı 125 DAKİKA (api/antivirus.ts). O süre boyunca ekranda
 * değişen tek şey düğmenin pasifleşmesiydi — düğmenin `disabled` olması "sürüyor" DEMEK
 * DEĞİLDİR. Kullanıcı ekranı donmuş sanıp sayfayı yeniliyor, taramayı ikinci kez tetikliyordu.
 * Şerit ne sürdüğünü ve GEÇEN SÜREYİ yazar.
 *
 * SAYFA YENİLEME ARTIK KAPALI: `/antivirus/status` süren taramayı da döndürüyor (`runningScan`),
 * bu yüzden yenilenmiş sayfa şeridi sunucudan geri okuyabiliyor. Bkz. `avServerScan` bloğu.
 *
 * KALAN SINIR (dürüstlük): bu geri okuma yalnız TARAMA için var. ClamAV KURULUMU ve TANIM
 * GÜNCELLEMESİ hâlâ yalnız bu sekmede görünür — onlar bileşen kurulum hattından geçiyor ve
 * sunucuda ayrı bir durum tutmuyorlar. O iki iş dakikalar sürer, tarama ise saatler; kapatılan
 * gerçek zarar buydu.
 */
async function installAv() {
  busyAction.value = "av";
  avTask.begin(t("panel.antivirus.runningInstall"));
  try {
    avStatus.value = await installAntivirus();
    showNotice(t("panel.antivirus.installed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    avTask.end();
    busyAction.value = "";
  }
}

async function updateAvDefs() {
  busyAction.value = "av";
  avTask.begin(t("panel.antivirus.runningDefs"));
  try {
    avStatus.value = await updateAntivirusDefinitions();
    showNotice(t("panel.antivirus.defsDone"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    avTask.end();
    busyAction.value = "";
  }
}

async function runAvScan() {
  if (!avSiteName.value || avServerScan.value !== null) return;
  busyAction.value = "av";
  avTask.begin(t("panel.antivirus.runningScan", { site: avSiteName.value }));
  try {
    const result = await scanAntivirus(avSiteName.value);
    await refreshAvStatus();
    showNotice(
      result.status === "infected"
        ? t("panel.antivirus.scanInfected", { count: result.infectedCount })
        : t("panel.antivirus.scanClean"),
      result.status === "infected");
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    avTask.end();
    busyAction.value = "";
  }
}

async function refreshAll() {
  loading.value = true;
  try {
    firewallRules.value = await getFirewallRules();
    firewallLoaded.value = true;
    await refreshBruteForce();
    if (selectedSiteName.value) {
      securityConfig.value = await getSiteSecurity(selectedSiteName.value);
      aclForm.principal = securityConfig.value.principal || aclForm.principal;
      rateForm.requestsPerMinute = securityConfig.value.requestsPerMinute || rateForm.requestsPerMinute;
    }
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function refreshBruteForce() {
  const status = await getBruteForce();
  applyBruteForce(status.settings, status.bannedIps);
}

function applyBruteForce(settings: BruteForceSettings, bans: BannedIp[]) {
  Object.assign(bruteForm, settings);
  bruteWhitelistText.value = settings.whitelistedIps.join(", ");
  bannedIps.value = bans;
  bruteForceLoaded.value = true;
}

async function saveBruteForce() {
  busyAction.value = "bruteforce";
  try {
    const whitelist = bruteWhitelistText.value
      .split(/[\s,]+/)
      .map((value) => value.trim())
      .filter((value) => value.length > 0);
    const status = await updateBruteForce({ ...bruteForm, whitelistedIps: whitelist });
    applyBruteForce(status.settings, status.bannedIps);
    showNotice(t("panel.bruteforce.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function unban(ipAddress: string) {
  busyAction.value = "unban";
  try {
    const status = await unbanIp(ipAddress);
    applyBruteForce(status.settings, status.bannedIps);
    showNotice(t("panel.bruteforce.unbanned"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function runSiteAction(action: "isolate" | "acl" | "rate" | "rules") {
  if (!selectedSiteName.value) return;
  busyAction.value = action;
  try {
    if (action === "isolate") securityConfig.value = await isolateSite(selectedSiteName.value);
    else if (action === "acl") securityConfig.value = await applyAcl(selectedSiteName.value, aclForm.principal, aclForm.rights);
    else if (action === "rate") securityConfig.value = await setRateLimit(selectedSiteName.value, rateForm.requestsPerMinute, 30);
    else securityConfig.value = await enableBasicRules(selectedSiteName.value);
    showNotice(t("panel.security.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function saveFirewall(allow: boolean) {
  busyAction.value = allow ? "allow" : "block";
  try {
    if (allow) await allowPort(firewallForm.port, firewallForm.protocol, firewallForm.ruleName);
    else await blockPort(firewallForm.port, firewallForm.protocol, firewallForm.ruleName);
    firewallRules.value = await getFirewallRules();
    firewallLoaded.value = true;
    showFirewallForm.value = false;
    showNotice(t("panel.firewall.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

/** Panelin kendi güvenlik duvarı kuralını kaldırır (yanlışlıkla eklenen engellemenin geri alma yolu). */
async function removeRule(ruleName: string) {
  await confirm({
    title: t("panel.firewall.confirmRemoveTitle", { name: ruleName }),
    message: t("panel.firewall.confirmRemove"),
    impact: [ruleName],
    action: async () => {
      busyAction.value = `remove:${ruleName}`;
      try {
        await removeFirewallRule(ruleName);
        firewallRules.value = await getFirewallRules();
        firewallLoaded.value = true;
        showNotice(t("panel.firewall.removed"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        busyAction.value = "";
      }
    }
  });
}

/*
 * SİTE GÜVENLİK ROZETİ — ÜÇ DURUM, TEK KAYNAK.
 *
 * `measured === false` DIŞINDA hiçbir şey "ölçülemedi" saymaz: alanı hiç göndermeyen eski bir
 * sunucuda değer `undefined` gelir ve ölçülmüş bir sonucu şüpheli göstermek, ters yönde bir
 * yalan olurdu (gerekçe api/security.ts'te).
 *
 * TON `warn`: ölçülemeyen bir koruma "iyi" (yeşil) de değildir, "kesin arıza" (kırmızı) da
 * değildir — panelin bilmediği bir şeydir. Panelin bilinmeyen için kullandığı ton bu.
 */
function stateLabel(value: boolean, measured?: boolean): string {
  if (measured === false) return t("panel.security.unmeasured");
  return value ? t("panel.status.enabled") : t("panel.status.disabled");
}

function stateTone(value: boolean, measured?: boolean): string {
  if (measured === false) return "warn";
  return value ? "ok" : "warn";
}

// Açıklama şeridi yalnız gerçekten ölçülemeyen bir satır varken çıksın diye tek yerde toplanır.
const hasUnmeasured = computed(() => {
  const config = securityConfig.value;
  if (!config) return false;
  return config.isIsolatedMeasured === false
    || config.folderAclMeasured === false
    || config.basicRulesMeasured === false
    || config.rateLimitMeasured === false;
});

/** Kural eyleminin sözcüğü — kural `utils/firewallAction.ts`te, burada yalnız `t` bağlanır. */
function actionLabel(action: string): string {
  return firewallActionLabel(t, action);
}

/** İzin = iyi, engel = uyarı, TANINMAYAN = nötr (rozetin varsayılanı "henüz bir şey demiyorum"). */
function actionTone(action: string): string {
  const kind = normalizeFirewallAction(action);
  if (kind === "allow") return "ok";
  return kind === "block" ? "warn" : "";
}

/** Tarama sonucunun tonu: bulaşma bir UYARI değil, kritik bir bulgudur (gerekçe şablonda). */
function scanTone(status: string): string {
  if (status === "clean") return "ok";
  return status === "infected" ? "danger" : "warn";
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
/* YIKICI EYLEMIN SABIT YERI: satirin sag ucu. Tek kural, cunku bu ekranda tek boyle eylem
   var (bkz. sablon). `.row-actions` bir flex satiri oldugu icin `auto` kenar bosluğu butun
   arta kalan yeri yutar ve dugme her panel genisliginde AYNI yerde durur. */
.destructive-slot {
  margin-left: auto;
}

/* SAYI SUTUNLARI SAGA HIZALI, RAKAMLAR TEK GENISLIKTE.
   OLCULDU: iki tabloda da bir sayi sutunu var — guvenlik duvarinda PORT (80 / 443 / 8890 /
   49152), kaba kuvvette DENEME SAYISI (3 / 12 / 147). Sola hizaliyken "8890" ile "80" ayni
   noktadan basliyor ve hangisinin buyuk oldugu ancak okunarak anlasiliyordu; saga hizali ve
   `tabular-nums` ile basamaklar alt alta gelir, karsilastirma bakisla olur.
   Basliklar da hizaya dahil (`.table-head` ayni `nth-child`e giriyor): baslik sutunundan
   kayarsa hangi sayinin ne oldugu bulaniklasir.
   `:nth-child` KULLANILIYOR, `:not()` degil — bu depoda ozgullugu yukselten ciplak `:not()`
   icin nobetci var (CssSpecificityGuardTests) ve niyet zaten "sadece su sutun". */
.firewall-row > :nth-child(3),
.bruteforce-row > :nth-child(2) {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.firewall-row {
  grid-template-columns: minmax(240px, 1.5fr) 0.6fr 0.7fr 0.7fr 0.8fr;
}

.bruteforce-row {
  grid-template-columns: minmax(160px, 1.2fr) 0.6fr 1fr 1fr auto;
}

.av-finding-row {
  grid-template-columns: minmax(280px, 2fr) 1fr;
}

.av-path {
  overflow-wrap: anywhere;
}
</style>
