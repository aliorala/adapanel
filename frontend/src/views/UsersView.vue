<template>
  <section class="panel module-shell users-workspace">
    <!-- Servis planları (panel): limit şablonu tanımla, müşteriye ata. Yalnız admin. -->
    <div v-if="isAdmin" class="form-surface plans-card">
      <!-- Ortak `.section-head` devralindi (base.css): kapsamli `.plans-head` kopyasi ayni uc
           ozelligi yaziyordu, tek eksigi `flex-wrap` idi — dar panelde baslik ile dugme ayni
           satirda birbirini eziyordu. Baslik `<strong>` (13.5px) yerine `<h3>` (15.9px):
           bu sayfada UC ayri bolum var ve hicbirinin baslik rutbesi yoktu. -->
      <div class="section-head">
        <div>
          <h3>{{ t("panel.plans.title") }}</h3>
          <p class="meta">{{ t("panel.plans.hint") }}</p>
        </div>
        <!-- IKINCIL: sayfanin birincil eylemi "Kullanici olustur". Ikisi de birincil maviyken
             yonetici hangisinin sayfanin isi oldugunu ayirt edemiyordu. -->
        <button type="button" class="secondary compact" @click="planFormunuAcKapa">
          {{ showPlanForm ? t("panel.common.cancel") : t("panel.plans.add") }}
        </button>
      </div>

      <div v-if="servicePlans.length" class="plans-grid">
        <article v-for="plan in servicePlans" :key="plan.id" class="plan-card">
          <div class="plan-card-head">
            <h4 class="plan-name">{{ plan.name }}</h4>
            <span class="plan-card-actions">
              <button type="button" class="secondary compact" @click="planiDuzenle(plan)">{{ t("panel.common.edit") }}</button>
              <!-- `secondary danger` BIRLIKTE yaziliyordu: base.css'te `.danger` sonra geldigi
                   icin dolgu zaten kirmizi ciziliyor, `.secondary`den geriye yalniz yersiz bir
                   gri KENAR kaliyordu. Tek sinif birakildi; gorunen fark o kenarin gitmesi. -->
              <button type="button" class="danger compact" @click="removeServicePlan(plan)">{{ t("panel.common.delete") }}</button>
            </span>
          </div>
          <ul class="plan-limits">
            <li><span>{{ t("panel.plans.sitesLabel") }}</span><b>{{ plan.maxSites || "∞" }}</b></li>
            <!-- PLAN KARTI bir ÖLÇÜM değil, alttaki formun YANKISIDIR: yönetici "1024" yazar,
                 kart "1024" gösterir. Bu yüzden sayı ÖLÇEKLENMEZ (kota göstergelerinden farkı
                 bu); düzeltilen tek şey ETİKET. Sunucu bu alanları 1024 tabanında yorumluyor
                 (FileQuotaGuard: MaxDiskMb * 1024 * 1024; SubscriptionEnforcementService:
                 MaxTrafficGbPerMonth * 1024³), yani doğru birim MiB ve GiB'dir — koda gömülü
                 "MB"/"GB" kullanıcıya %7 yanlış bir taban söylüyordu. -->
            <li><span>{{ t("panel.plans.diskLabel") }}</span><b>{{ plan.maxDiskMb ? plan.maxDiskMb + " " + t("panel.units.mib") : "∞" }}</b></li>
            <li><span>{{ t("panel.plans.databasesLabel") }}</span><b>{{ plan.maxDatabases || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.mailboxesLabel") }}</span><b>{{ plan.maxMailboxes || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.scheduledTasksLabel") }}</span><b>{{ plan.maxScheduledTasks || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.ftpLabel") }}</span><b>{{ plan.maxFtpUsers || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.trafficLabel") }}</span><b>{{ plan.maxTrafficGbPerMonth ? plan.maxTrafficGbPerMonth + " " + t("panel.units.gib") : "∞" }}</b></li>
            <li><span>{{ t("panel.plans.validityLabel") }}</span><b>{{ plan.validityDays ? plan.validityDays + " " + t("panel.plans.days") : "∞" }}</b></li>
            <li><span>{{ t("panel.plans.backupsLabel") }}</span><b>{{ plan.maxBackupsPerDay || "∞" }}</b></li>
          </ul>
        </article>
      </div>
      <!-- Planlar gelmeden "plan yok" yazıyordu; tanımlı servis planları yokmuş gibi görünüyordu. -->
      <p v-else-if="!plansLoaded" class="empty-state plans-empty" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <!-- ÇEKİLEMEDİ ile YOK ayrı gösterilir; ikisini birleştirmek yöneticiye var olan planları
           yok diye sunmak olurdu. -->
      <p v-else-if="plansError" class="notice error" role="alert">{{ plansError }}</p>
      <p v-else class="empty-state plans-empty">{{ t("panel.plans.empty") }}</p>

      <form v-if="showPlanForm" class="plan-form-grid" @submit.prevent="planiKaydet">
        <!-- Plan ABONELIKLERE BAGLI: duzenlemek bagli butun musterilerin limitlerini degistirir.
             Bunu sessizce yapmak, bir kotayi farkinda olmadan dusurmeye yol acardi. -->
        <!-- `notice warn` HICBIR YERDE TANIMLI DEGILDI (yalniz AppStoreView'in kendi kapsamli
             stilinde var; kapsamli stil bilesenler arasi gecmez): bu uyari MAVI bilgi seridi
             olarak ciziliyordu, yani "bagli butun musterilerin limiti degisecek" uyarisi bir
             bilgi notundan ayirt edilemiyordu. Ortak `warn-banner` kehribar cizer. -->
        <p v-if="duzenlenenPlanId" class="notice warn-banner form-note">{{ t("panel.plans.editWarning") }}</p>
        <label class="stacked-field plan-name-cell">
          <span class="field-label">{{ t("panel.plans.name") }}</span>
          <input v-model.trim="planForm.name" :placeholder="t('panel.plans.namePlaceholder')" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.users.maxSites") }}</span>
          <input v-model.number="planForm.maxSites" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.users.maxDiskMb") }}</span>
          <input v-model.number="planForm.maxDiskMb" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.users.maxDatabases") }}</span>
          <input v-model.number="planForm.maxDatabases" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.users.maxMailboxes") }}</span>
          <input v-model.number="planForm.maxMailboxes" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.users.maxScheduledTasks") }}</span>
          <input v-model.number="planForm.maxScheduledTasks" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.plans.ftpLabel") }}</span>
          <input v-model.number="planForm.maxFtpUsers" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.plans.trafficLabel") }}</span>
          <input v-model.number="planForm.maxTrafficGbPerMonth" type="number" min="0" />
          <small class="field-hint">{{ t("panel.plans.trafficHint") }}</small>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.plans.validityLabel") }}</span>
          <input v-model.number="planForm.validityDays" type="number" min="0" />
          <small class="field-hint">{{ t("panel.plans.validityHint") }}</small>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.plans.maxBackupsPerDay") }}</span>
          <input v-model.number="planForm.maxBackupsPerDay" type="number" min="0" max="24" />
          <small class="field-hint">{{ t("panel.plans.backupsHint") }}</small>
        </label>
        <p class="hint form-note">{{ t("panel.plans.formHint") }}</p>
        <div class="form-actions">
          <button type="submit" class="compact" :disabled="!planForm.name || planKaydediliyor">
            {{ duzenlenenPlanId ? t("panel.common.save") : t("panel.plans.add") }}
          </button>
        </div>
      </form>
    </div>

    <div class="module-toolbar">
      <div>
        <h3>{{ t("panel.users.title") }}</h3>
        <p class="meta">{{ t("panel.settings.usersHint") }}</p>
      </div>
      <!-- Ayni dugme hem "Olustur" hem "Iptal": IPTAL ASLA BIRINCIL DEGILDIR. Form aciklen
           birincil eylem asagidaki gonder dugmesidir; ust dugme o an ikincile duser, boylece
           ekranda her durumda TEK mavi dugme kalir. -->
      <button :class="['compact', { secondary: showUserCreateForm }]" @click="showUserCreateForm = !showUserCreateForm">
        {{ showUserCreateForm ? t("panel.common.cancel") : t("panel.users.create") }}
      </button>
    </div>

    <!-- `user-create-grid` KALDIRILDI: yaptigi tek sey ortak `.form-grid`in zaten verdigi
         3 sutunu tekrar yazmak, sonra da 980px'te DOGRUDAN tek sutuna dusmekti. Ortak merdiven
         3 -> 2 (900px) -> 1 (720px) seklinde; yani 900-980 arasinda bu form panelin geri
         kalanindan farkli davraniyor, tek sutuna dusup gereksiz yere uzuyordu. -->
    <form v-if="showUserCreateForm" class="form-surface form-grid" @submit.prevent="createUser">
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.users.userName") }}</span>
        <input v-model="createUserForm.userName" minlength="3" maxlength="32" required />
      </label>
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.form.email") }}</span>
        <input v-model="createUserForm.email" type="email" required />
      </label>
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.form.password") }}</span>
        <PasswordField v-model="createUserForm.password" minlength="12" required />
      </label>
      <label v-if="isAdmin" class="stacked-field">
        <span class="field-label">{{ t("panel.users.role") }}</span>
        <select v-model="createUserForm.role">
          <option value="Administrator">{{ t("panel.role.administrator") }}</option>
          <option value="Operator">{{ t("panel.role.operator") }}</option>
          <option value="Reseller">{{ t("panel.role.reseller") }}</option>
          <option value="Customer">{{ t("panel.role.customer") }}</option>
        </select>
      </label>
      <label v-if="isAdmin && createUserForm.role === 'Customer' && parentCandidates.length > 0" class="stacked-field">
        <span class="field-label">{{ t("panel.users.parentSubscription") }}</span>
        <select v-model="createUserForm.parentUserId">
          <option value="">{{ t("panel.users.parentSubscriptionNone") }}</option>
          <option v-for="candidate in parentCandidates" :key="candidate.id" :value="candidate.id">{{ candidate.userName }}</option>
        </select>
        <small class="field-hint">{{ t("panel.users.parentSubscriptionHint") }}</small>
      </label>
      <label v-if="isAdmin && createUserForm.role === 'Customer' && !createUserForm.parentUserId && resellers.length > 0" class="stacked-field">
        <span class="field-label">{{ t("panel.users.owner") }}</span>
        <select v-model="createUserForm.ownerId">
          <option value="">{{ t("panel.users.ownerNone") }}</option>
          <option v-for="reseller in resellers" :key="reseller.id" :value="reseller.id">{{ reseller.userName }}</option>
        </select>
        <small class="field-hint">{{ t("panel.users.ownerHint") }}</small>
      </label>
      <div v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field customer-sites">
        <span class="field-label">{{ t("panel.users.assignedSites") }}</span>
        <label v-for="site in sitesStore.sites" :key="site.name" class="checkbox">
          <input v-model="createUserForm.assignedSites" type="checkbox" :value="site.name" />
          <span>{{ site.name }}</span>
        </label>
        <small v-if="sitesStore.sites.length === 0" class="field-hint">{{ t("panel.users.noSitesToAssign") }}</small>
      </div>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId && servicePlans.length > 0" class="stacked-field">
        <span class="field-label">{{ t("panel.plans.select") }}</span>
        <select v-model="createPlanId" @change="applyPlanToCreate">
          <option value="">{{ t("panel.plans.custom") }}</option>
          <option v-for="plan in servicePlans" :key="plan.id" :value="plan.id">{{ plan.name }}</option>
        </select>
      </label>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxSites") }}</span>
        <input v-model.number="createUserForm.maxSites" type="number" min="0" />
      </label>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxDiskMb") }}</span>
        <input v-model.number="createUserForm.maxDiskMb" type="number" min="0" />
      </label>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxDatabases") }}</span>
        <input v-model.number="createUserForm.maxDatabases" type="number" min="0" />
      </label>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxMailboxes") }}</span>
        <input v-model.number="createUserForm.maxMailboxes" type="number" min="0" />
      </label>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxScheduledTasks") }}</span>
        <input v-model.number="createUserForm.maxScheduledTasks" type="number" min="0" />
      </label>
      <p v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="hint form-note">{{ t("panel.plans.formHint") }}</p>
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.form.language") }}</span>
        <select v-model="createUserForm.preferredLanguage">
          <option value="tr">{{ t("panel.language.tr") }}</option>
          <option value="en">{{ t("panel.language.en") }}</option>
        </select>
      </label>
      <!-- `welcome-cell` HICBIR SEY YAPMIYORDU (kapsamli stilde tanimi yoktu); kaldirildi. -->
      <label class="check-field">
        <input v-model="createUserForm.sendWelcomeEmail" type="checkbox" />
        <span>{{ t("panel.users.sendWelcomeEmail") }}</span>
      </label>
      <!-- Gonder dugmesi bir IZGARA HUCRESI degil, formun KAPANIS SATIRI: tam genislikte ve
           saga hizali. Onceden ucuncu sutunda, komsu bir alanin hizasinda duruyordu ve alan
           sayisi role gore degistigi icin her rolde baska bir yere kayiyordu. -->
      <div class="form-actions">
        <button type="submit" class="compact">{{ t("panel.users.create") }}</button>
      </div>
    </form>

    <div class="data-table">
      <div class="data-row table-head user-row">
        <span>{{ t("panel.users.userName") }}</span>
        <span>{{ t("panel.users.role") }}</span>
        <span>{{ t("panel.users.status") }}</span>
        <span>{{ t("panel.users.twoFactor") }}</span>
        <span>{{ t("panel.software.action") }}</span>
      </div>
      <div v-for="user in authStore.users" :key="user.id" class="data-row user-row">
        <div class="cell-main">
          <strong>{{ user.userName }}</strong>
          <small>{{ user.email }}</small>
          <small v-if="user.role === 'Customer'" class="customer-meta">
            {{ user.assignedSites.length }} {{ t("panel.users.sitesShort") }}
            <!-- Ölçüm KESİLDİYSE sayı bir ALT SINIRDIR; rozetsiz göstermek onu tam değer gibi
                 sunmak olurdu. Bayrak SATIR BAŞINA gelir: bütçe abonelik başına olduğu için bir
                 müşterinin ölçümü kesilirken komşusununki tam olabilir. -->
            · {{ formatQuotaMib(user.diskUsageMb) }} / {{ user.maxDiskMb > 0 ? formatQuotaMib(user.maxDiskMb) : "∞" }}<span
              v-if="!user.diskMeasured"
              class="status-pill warn"
              :title="t('users.disk_usage_partial_hint')">{{ t("users.disk_usage_partial") }}</span>
            · {{ user.databaseCount }} / {{ user.maxDatabases > 0 ? user.maxDatabases : "∞" }} {{ t("panel.users.databasesShort") }}
            <template v-if="user.ownerName"> · {{ t("panel.users.owner") }}: {{ user.ownerName }}</template>
            <template v-if="user.parentName"> · {{ t("panel.users.subUserOf") }}: {{ user.parentName }}</template>
          </small>
        </div>
        <span>{{ user.role }}</span>
        <!-- "Kilitli" rozeti DURUM hücresinin İÇİNDE. Önceden ayrı bir ızgara çocuğuydu ve
             başlık 5 sütunluk olduğu için, bir kullanıcı kilitlendiğinde satır 6 hücreye çıkıp
             "İşlem" düğmelerini alt satıra atıyordu — tam da acilen "Kilidi aç"a basılması
             gereken satır bozuluyordu. -->
        <span class="user-status-cell">
          <span :class="['status-pill', user.isActive ? 'ok' : 'warn']">
            {{ user.isActive ? t("panel.status.active") : t("panel.status.inactive") }}
          </span>
          <span v-if="user.lockedUntilUtc" class="status-pill danger" :title="t('panel.users.lockedHint')">
            {{ t("panel.users.locked") }}
          </span>
        </span>
        <span :class="['status-pill', user.isTwoFactorEnabled ? 'ok' : 'warn']">
          {{ user.isTwoFactorEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
        </span>
        <!-- EYLEM SERIDI IKI BOLGEYE AYRILDI. Onceden dort dugme tek bir sirada, saga yasli
             duruyordu ve gorunen dugme sayisi satira gore 2-4 arasinda degisiyordu (kilit
             yalnizca kilitli hesapta, "Giris yap" yalnizca musteride, "Sil" kendi hesabinda
             hic yok). Sonuc: bir satirda "Sil"in bulundugu nokta, bir alt satirda "Duzenle"
             oluyordu. Yikici bolge artik KOSULSUZ ciziliyor (icindeki dugme kosullu), yani
             "Sil" her satirda AYNI sutunda. -->
        <span class="user-row-actions">
          <span class="row-tools">
            <!-- Hatalı giriş kilidi: beklemeden aç (doğru parolayla da kalkar, bu kısayol). -->
            <button
              v-if="user.lockedUntilUtc"
              class="secondary compact row-button"
              :disabled="unlockingUserId === user.id"
              @click="unlockUser(user)">
              {{ unlockingUserId === user.id ? t("panel.users.unlocking") : t("panel.users.unlock") }}
            </button>
            <button
              v-if="user.role === 'Customer'"
              class="secondary compact row-button"
              @click="impersonateUser(user)">
              {{ t("panel.users.loginAs") }}
            </button>
            <button class="secondary compact row-button" @click="selectUser(user)">{{ t("panel.users.edit") }}</button>
          </span>
          <span class="row-danger">
            <button
              v-if="user.id !== authStore.session?.user?.id"
              class="danger compact row-button"
              @click="deleteUser(user)">
              {{ t("panel.users.delete") }}
            </button>
          </span>
        </span>
      </div>
    </div>

    <!-- ABONELİK: plan bağı, kullanım ve askı denetimi seçili müşterinin yanında.
         Kota/askı işlemleri kullanıcı düzenlemenin parçasıdır; ayrı bir ekrana göndermek
         yöneticiyi iki yer arasında gezdirirdi. -->
    <div v-if="selectedUserId && selectedUserIsCustomer" class="form-surface">
      <SubscriptionCard :user-id="selectedUserId" manageable />
    </div>

    <!-- `user-edit-grid` KALDIRILDI: `align-items: end` alanlari ALTTAN hizaliyordu, oysa
         formda alti ipuclu alanlar var; alt hizalama o hucrelerin etiketlerini yukari kacirip
         basliklari kademeli gosteriyordu (ayni hata plan formunda da vardi, orada `start` ile
         cozulmustu). Ortak `.form-grid`in varsayilan hizasi dogru olan. -->
    <form v-if="selectedUserId" class="form-surface form-grid" @submit.prevent="saveUserChanges">
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.form.email") }}</span>
        <input v-model="selectedUserForm.email" type="email" required />
      </label>
      <label v-if="isAdmin" class="stacked-field">
        <span class="field-label">{{ t("panel.users.role") }}</span>
        <select v-model="selectedUserForm.role">
          <option value="Administrator">{{ t("panel.role.administrator") }}</option>
          <option value="Operator">{{ t("panel.role.operator") }}</option>
          <option value="Reseller">{{ t("panel.role.reseller") }}</option>
          <option value="Customer">{{ t("panel.role.customer") }}</option>
        </select>
      </label>
      <p v-if="selectedUserForm.parentUserId" class="hint form-note">{{ t("panel.users.subUserScopeNote") }}</p>
      <label v-if="isAdmin && selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId && resellers.length > 0" class="stacked-field">
        <span class="field-label">{{ t("panel.users.owner") }}</span>
        <select v-model="selectedUserForm.ownerId">
          <option value="">{{ t("panel.users.ownerNone") }}</option>
          <option v-for="reseller in resellers" :key="reseller.id" :value="reseller.id">{{ reseller.userName }}</option>
        </select>
      </label>
      <div v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field customer-sites">
        <span class="field-label">{{ t("panel.users.assignedSites") }}</span>
        <label v-for="site in sitesStore.sites" :key="site.name" class="checkbox">
          <input v-model="selectedUserForm.assignedSites" type="checkbox" :value="site.name" />
          <span>{{ site.name }}</span>
        </label>
        <small v-if="sitesStore.sites.length === 0" class="field-hint">{{ t("panel.users.noSitesToAssign") }}</small>
      </div>
      <label v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxSites") }}</span>
        <input v-model.number="selectedUserForm.maxSites" type="number" min="0" />
      </label>
      <label v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxDiskMb") }}</span>
        <input v-model.number="selectedUserForm.maxDiskMb" type="number" min="0" />
      </label>
      <label v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxDatabases") }}</span>
        <input v-model.number="selectedUserForm.maxDatabases" type="number" min="0" />
      </label>
      <label v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxMailboxes") }}</span>
        <input v-model.number="selectedUserForm.maxMailboxes" type="number" min="0" />
      </label>
      <label v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxScheduledTasks") }}</span>
        <input v-model.number="selectedUserForm.maxScheduledTasks" type="number" min="0" />
      </label>
      <p v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="hint form-note">{{ t("panel.plans.formHint") }}</p>
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.form.language") }}</span>
        <select v-model="selectedUserForm.preferredLanguage">
          <option value="tr">{{ t("panel.language.tr") }}</option>
          <option value="en">{{ t("panel.language.en") }}</option>
        </select>
      </label>
      <label class="check-field">
        <input v-model="selectedUserForm.isActive" type="checkbox" />
        <span>{{ t("panel.users.isActive") }}</span>
      </label>
      <!-- SIRA DEGISTI: ikincil solda, birincil SAGDA. Panelin geri kalaninda (ve onay
           kutusunda) gonder dugmesi hep en sagda duruyor; burada tersiydi, yani "Kaydet"in
           yeri bu tek formda farkliydi. -->
      <div class="row-actions form-actions">
        <button type="button" class="secondary compact" @click="selectedUserId = ''">{{ t("panel.common.cancel") }}</button>
        <button type="submit" class="compact">{{ t("panel.users.save") }}</button>
      </div>
    </form>
  </section>
</template>

<script setup lang="ts">
// Hesap yönetimi (Plesk "Müşteriler & Bayiler"): kullanıcı listesi, oluşturma/düzenleme/silme,
// kilit açma, müşteri olarak giriş ve alt-kullanıcı (abonelik altı hesap) bağlama.
// Daha önce SetupWizardView > Ayarlar > Kullanıcılar sekmesindeydi; davranış birebir korunmuştur.
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { getServicePlans, createServicePlan, updateServicePlan, deleteServicePlan, type ServicePlan } from "../api/servicePlans";
import { extractApiDetails, extractApiMessage, useAuthStore, type ManagedUser } from "../stores/auth";
import { extractDeletionLeftovers, type UserDeletionLeftover } from "../api/users";
import SubscriptionCard from "../components/SubscriptionCard.vue";
import { useSitesStore } from "../stores/sites";
import { formatBytes as sharedFormatBytes } from "../utils/bytes";

// Bildirimler üst bandda (MainLayout > banner) gösterilsin diye kabuk bileşene iletilir.
const emit = defineEmits<{ (event: "notify", message: string, isError: boolean): void }>();

const i18n = useI18n();
// params: "'{name}' planı silinecek" gibi yer tutuculu metinler için. Verilmezse davranış aynı.
const t = (key: string, params?: Record<string, unknown>) => {
  try {
    const value = params ? i18n.t(key, params) : i18n.t(key);
    if (typeof value !== "string") {
      console.error("Unexpected i18n value", key, value);
      return key;
    }

    return value;
  } catch (error) {
    console.error("Failed i18n key", key, error);
    return key;
  }
};

const authStore = useAuthStore();
const sitesStore = useSitesStore();
const { confirm } = useConfirm();

const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const isReseller = computed(() => authStore.session?.user?.role === "Reseller");

/**
 * `...Mb` ADI YANILTICI: sunucu bu sayıyı `bayt / (1024*1024)` ile üretiyor
 * (`CustomerScopeService.GetDiskUsageMbAsync`) ve kotayı `MaxDiskMb * 1024 * 1024` ile
 * uyguluyor (`FileQuotaGuard`) — yani alan MiB taşır, MB değil.
 */
const MIB = 1024 * 1024;

/**
 * MiB taşıyan sunucu alanı → bayt → panelin ORTAK biçimleyicisi ("gauge" hassasiyetiyle).
 *
 * NEDEN "gauge": buradaki sayı bir liste dolgusu değil, müşterinin SATIN ALDIĞI sınıra ne kadar
 * yaklaştığını gösteren kota okumasıdır; SubscriptionCard ve SiteHostingView'daki kota
 * göstergeleriyle aynı gerekçe ve aynı davranış (bkz. utils/bytes.ts). Bir kota ondalıklı,
 * diğeri ondalıksız görünemez.
 *
 * NEDEN ÇEVİRİYORUZ: satır önceden "512 / 5120 MB" basıyordu — sayılar 1024 tabanlı ama etiket
 * "MB" diyordu, yani kullanıcı %7 yanlış bir tabanla okuyordu.
 */
function formatQuotaMib(value: number) {
  return sharedFormatBytes(value * MIB, t, "gauge");
}

const selectedUserId = ref("");
const selectedUserIsCustomer = computed(() =>
  authStore.users.some((user) => user.id === selectedUserId.value && user.role === "Customer")
);
const unlockingUserId = ref("");
const showUserCreateForm = ref(false);
const showPlanForm = ref(false);

const createUserForm = reactive({
  userName: "",
  email: "",
  password: "",
  // Bayi YALNIZ müşteri oluşturabilir → rol seçimi gizli ve Customer'a sabit.
  role: isReseller.value ? "Customer" : "Operator",
  ownerId: "",
  assignedSites: [] as string[],
  maxSites: 0,
  maxDiskMb: 0,
  maxDatabases: 0,
  maxMailboxes: 0,
  maxScheduledTasks: 0,
  parentUserId: "",
  sendWelcomeEmail: false,
  preferredLanguage: "tr"
});

const selectedUserForm = reactive({
  email: "",
  role: "Operator",
  ownerId: "",
  assignedSites: [] as string[],
  maxSites: 0,
  maxDiskMb: 0,
  maxDatabases: 0,
  maxMailboxes: 0,
  maxScheduledTasks: 0,
  parentUserId: "",
  preferredLanguage: "tr",
  isActive: true
});

// Müşteriye sahip atamak için bayi (Reseller) kullanıcı listesi.
const resellers = computed(() => authStore.users.filter((user) => user.role === "Reseller"));

// Alt-kullanıcı için "ana abonelik" adayları: kendisi alt-kullanıcı OLMAYAN (bağımsız) müşteriler.
const parentCandidates = computed(() => authStore.users.filter((user) => user.role === "Customer" && !user.parentUserId));

// Servis planları (Servis Planlari): müşterilere atanabilen limit şablonları.
const servicePlans = ref<ServicePlan[]>([]);
// Yüklenmemiş plan listesi "plan yok" olarak görünüyordu.
const plansLoaded = ref(false);
/** Plan listesi ÇEKİLEMEDİ. Boş listeyle karıştırılmamalı: biri "yok", diğeri "bilmiyoruz". */
const plansError = ref("");
// Formun BOS hali tek kaynakta: hem baslangic hem de sifirlama bunu kullanir. Iki ayri elle
// yazilmis liste tutmak, birinin guncellenip digerinin unutulmasi demekti.
const BOS_PLAN_FORMU = {
  name: "",
  maxSites: 0,
  maxDiskMb: 0,
  maxDatabases: 0,
  maxMailboxes: 0,
  maxScheduledTasks: 0,
  maxFtpUsers: 0,
  maxTrafficGbPerMonth: 0,
  validityDays: 0,
  // Bu alan formda YOKSA `ServicePlanRequest.MaxBackupsPerDay` her kayıtta 0'a düşer ve
  // aboneliklerin günlük yedek kotası sessizce kalkar. Aynı sınıftan bir unutma trafik kotasını
  // ölü bırakmıştı; istek `planForm`un KENDİSİNDEN türediği için alan burada tanımlı olmalıdır.
  maxBackupsPerDay: 0
};
const planForm = reactive({ ...BOS_PLAN_FORMU });
/** Dolu ise form YENI plan degil, bu planin duzenlemesidir. */
const duzenlenenPlanId = ref<string | null>(null);
const planKaydediliyor = ref(false);
const createPlanId = ref("");

onMounted(async () => {
  // Kabuk (SetupWizardView) admin/bayi girişinde listeyi zaten doldurur; doğrudan bu ekrana
  // düşüldüğünde (veya liste boşsa) kendi verisini çeker.
  if (authStore.users.length === 0) {
    await authStore.fetchUsers().catch((error: unknown) => notify(resolveApiMessage(error), true));
  }

  if (sitesStore.sites.length === 0) {
    await sitesStore.fetchSites().catch(() => undefined);
  }

  if (isAdmin.value) {
    await loadServicePlans();
  }
});

async function loadServicePlans() {
  // HATA YUTULMUYORDU: `.catch(() => [])` istek başarısız olduğunda BOŞ liste döndürüyor,
  // ekran da "tanımlı servis planı yok" yazıyordu. Yani var olan planlar YOK gibi görünüyor,
  // yönetici yenisini oluşturmaya kalkıyor ve aynı adla çakışma alıyordu. "Ölçemedik" ile
  // "yok" ayrı şeyler; panelin geri kalanı bu ayrımı yapıyor, burası yapmıyordu.
  try {
    servicePlans.value = await getServicePlans();
    plansError.value = "";
  } catch (error) {
    servicePlans.value = [];
    plansError.value = resolveApiMessage(error);
  } finally {
    plansLoaded.value = true;
  }
}

// Hata yakalama YOKTU: sunucu planı reddettiğinde (aynı ad, geçersiz kota) form sessizce
// kapanıyor ve kullanıcı planın oluştuğunu sanıyordu. Form ancak istek BAŞARILI dönünce sıfırlanır.
function planFormunuSifirla() {
  Object.assign(planForm, BOS_PLAN_FORMU);
  duzenlenenPlanId.value = null;
}

function planFormunuAcKapa() {
  if (showPlanForm.value) {
    showPlanForm.value = false;
    // Kapatirken duzenleme kipinden de cikilir; aksi halde bir sonraki "Plan ekle" tiklamasi
    // eski planin degerleriyle dolu bir form acar ve kullanici yeni plan olusturdugunu sanirdi.
    planFormunuSifirla();
    return;
  }
  planFormunuSifirla();
  showPlanForm.value = true;
}

function planiDuzenle(plan: ServicePlan) {
  duzenlenenPlanId.value = plan.id;
  // Alanlar tek tek degil, planin KENDISINDEN kopyalaniyor: yeni bir limit eklendiginde
  // burada da unutulup bos gitmesin.
  Object.assign(planForm, BOS_PLAN_FORMU);
  for (const alan of Object.keys(BOS_PLAN_FORMU) as (keyof typeof BOS_PLAN_FORMU)[]) {
    const deger = (plan as unknown as Record<string, unknown>)[alan];
    if (deger !== undefined && deger !== null) {
      (planForm as Record<string, unknown>)[alan] = deger;
    }
  }
  showPlanForm.value = true;
}

async function planiKaydet() {
  if (!planForm.name.trim()) {
    return;
  }

  planKaydediliyor.value = true;
  try {
    // Alanlari ELLE saymak tam da su hataya yol acmisti: plana FTP hesabi, aylik trafik ve
    // gecerlilik eklendi, bu liste guncellenmedi. Kullanici "50 GB" yaziyordu, istege 0 gidiyordu,
    // API "basarili" donuyordu ve kota konuldugu sanilirken hicbir zaman uygulanmiyordu.
    // Istek artik formun KENDISINDEN turuyor: yeni alan eklenir eklenmez gonderilir.
    const istek = { ...planForm, name: planForm.name.trim() };
    if (duzenlenenPlanId.value) {
      await updateServicePlan(duzenlenenPlanId.value, istek);
    } else {
      await createServicePlan(istek);
    }
    planFormunuSifirla();
    showPlanForm.value = false;
    await loadServicePlans();
  } catch (error) {
    notify(resolveApiMessage(error), true);
  } finally {
    planKaydediliyor.value = false;
  }
}

/**
 * Onay + BAŞARI bildirimi eklendi. Öncesinde ikisi de yoktu: kullanıcı tek tıkla planı siliyor,
 * ekranda hiçbir şey yazmıyordu — "sildim mi, silemedim mi?" ayırt edilemiyordu.
 */
async function removeServicePlan(plan: ServicePlan) {
  await confirm({
    title: t("panel.plans.confirmDeleteTitle"),
    message: t("panel.plans.confirmDeleteMessage", { name: plan.name }),
    impact: [plan.name],
    action: async () => {
      try {
        await deleteServicePlan(plan.id);
        await loadServicePlans();
        notify(t("panel.plans.deleted"), false);
      } catch (error) {
        notify(resolveApiMessage(error), true);
      }
    }
  });
}

function applyPlanToCreate() {
  const plan = servicePlans.value.find((item) => item.id === createPlanId.value);
  if (!plan) {
    return;
  }
  // Alanlar ELLE sayılmıyor: plan ile formun ORTAK limit alanları taranıyor. Elle yazılan liste,
  // plana yeni bir limit eklendiğinde sessizce eksik kalır ve kullanıcı planı seçtiği hâlde
  // o limitin dolmadığını fark etmezdi.
  for (const alan of Object.keys(plan) as (keyof typeof plan)[]) {
    if (alan === "id" || alan === "name") {
      continue;
    }
    if (alan in createUserForm && typeof plan[alan] === "number") {
      (createUserForm as unknown as Record<string, number>)[alan] = plan[alan] as number;
    }
  }
}

async function createUser() {
  try {
    await authStore.createUser({ ...createUserForm, ownerId: createUserForm.ownerId || null, parentUserId: createUserForm.parentUserId || null });
    createUserForm.userName = "";
    createUserForm.email = "";
    createUserForm.password = "";
    // Bayi YALNIZ müşteri oluşturabilir → rolü Customer'da tut (yoksa kota/site/plan alanları gizlenir).
    createUserForm.role = isReseller.value ? "Customer" : "Operator";
    createUserForm.ownerId = "";
    createUserForm.assignedSites = [];
    createUserForm.maxSites = 0;
    createUserForm.maxDiskMb = 0;
    createUserForm.maxDatabases = 0;
    createUserForm.maxMailboxes = 0;
    createUserForm.maxScheduledTasks = 0;
    createUserForm.parentUserId = "";
    createUserForm.sendWelcomeEmail = false;
    createUserForm.preferredLanguage = "tr";
    showUserCreateForm.value = false;
    notify(t("panel.users.created"), false);
  } catch (error) {
    notify(resolveApiMessage(error), true);
  }
}

async function impersonateUser(user: ManagedUser) {
  try {
    await authStore.impersonate(user.id);
    // Müşteri arayüzüne temiz geçiş için paneli yeniden yükle.
    window.location.reload();
  } catch (error) {
    // Hata YUTULMAZ. Önceden sessizce geçiliyordu: istek 403/kilitli hesap/ağ hatasıyla dönünce
    // reload çalışmıyor, ekranda tek satır uyarı çıkmıyordu — kullanıcı düğmeye basıp hiçbir şey
    // olmadığını görüyor ve tekrar tekrar basıyordu. Bu dosyadaki diğer tüm işlemler zaten
    // notify(...) kullanıyor; burası tek istisnaydı.
    notify(resolveApiMessage(error), true);
  }
}

// Kullanıcı silme (Plesk paritesi): varsayılan davranış siteleri/verileri SUNUCUDA BIRAKIR (yönetime devir).
// Site+dosya+DB'yi de silmek isteyen admin, Web Siteleri ekranındaki cascade silmeyi kullanır.
async function deleteUser(user: ManagedUser) {
  // Yüksek riskli mod: kullanıcı silme alt-kullanıcıları, cron görevlerini ve DB sahipliklerini de
  // temizler. Ne kaybedileceğini SAYARAK göster ve adı yazdır — refleksle onaylanacak bir işlem değil.
  const impact = [
    `${user.userName} · ${user.email}`,
    `${t("panel.users.sitesShort")}: ${user.assignedSites?.length ?? 0}`,
    `${t("panel.users.databasesShort")}: ${user.databaseCount ?? 0}`
  ];

  await confirm({
    title: t("panel.confirm.deleteUserTitle"),
    // Yerel t() sarmalayıcısı parametre almaz; ad enterpolasyonu için i18n.t doğrudan kullanılır.
    message: i18n.t("panel.users.confirmDelete", { name: user.userName }) as string,
    impact,
    requireText: user.userName,
    action: async () => {
      try {
        const sonuc = await authStore.deleteUser(user.id);
        if (selectedUserId.value === user.id) {
          selectedUserId.value = "";
        }

        // KALINTILAR ARTIK GÖRÜNÜYOR. Silme başarılı olsa bile sunucuda kalanlar (site klasörleri,
        // veritabanları) ADIYLA yazılır: eskiden ekran "kullanıcı silindi" deyip susuyordu ve
        // yönetici, sahibi kalmamış onlarca kaynağın orada durduğunu hiç öğrenmiyordu.
        const kalintilar = sonuc?.leftovers ?? [];
        notify(`${t("panel.users.deleted")}${kalintiMetni(kalintilar)}`, false);
      } catch (error) {
        // 409 (kısmi silme) da buraya düşer; gövdedeki kalıntı listesi hata metnine eklenir.
        notify(`${resolveApiMessage(error)}${kalintiMetni(extractDeletionLeftovers(error))}`, true);
      }
    }
  });
}

/**
 * Kalıntı listesini okunabilir satırlara çevirir. Boş listede BOŞ dizge döner ki mesaj
 * gereksiz yere şişmesin.
 *
 * "Bir şeyler kaldı" demek yöneticiyi aramaya gönderir; burada TİP + AD yazılır (hangi site,
 * hangi veritabanı, hangi alan adı) çünkü temizliği yapacak kişinin ihtiyacı olan tek şey odur.
 */
function kalintiMetni(leftovers: UserDeletionLeftover[]): string {
  if (leftovers.length === 0) {
    return "";
  }

  const satirlar = leftovers.map((leftover) => {
    const site = leftover.siteName ? `${leftover.siteName} › ` : "";
    // Sunucu yeni bir kaynak tipi eklerse ekranda HAM ANAHTAR görünmesin: çeviri yoksa
    // genel "Kaynak" başlığına düşülür — ad ve sebep zaten yazılıyor, bilgi kaybolmaz.
    const tipAnahtari = `panel.users.leftoverKind.${leftover.kind}`;
    const cevrilen = t(tipAnahtari);
    const tip = cevrilen === tipAnahtari ? t("panel.users.leftoverKind.other") : cevrilen;
    const bilincli = leftover.deliberate ? ` (${t("panel.users.leftoverDeliberate")})` : "";
    return `• ${site}${tip}: ${leftover.name} — ${leftover.reason}${bilincli}`;
  });

  return `\n\n${t("panel.users.leftoversTitle")}\n${satirlar.join("\n")}`;
}

// Hatalı giriş kilidini beklemeden açar (yönetici kısayolu; bayi yalnız kendi müşterisini açabilir).
async function unlockUser(user: ManagedUser) {
  unlockingUserId.value = user.id;
  try {
    await authStore.unlockUser(user.id);
    notify(t("panel.users.unlocked"), false);
  } catch (error) {
    notify(resolveApiMessage(error), true);
  } finally {
    unlockingUserId.value = "";
  }
}

function selectUser(user: ManagedUser) {
  selectedUserId.value = user.id;
  selectedUserForm.email = user.email;
  selectedUserForm.role = user.role;
  selectedUserForm.ownerId = user.ownerId ?? "";
  selectedUserForm.assignedSites = [...(user.assignedSites ?? [])];
  selectedUserForm.maxSites = user.maxSites ?? 0;
  selectedUserForm.maxDiskMb = user.maxDiskMb ?? 0;
  selectedUserForm.maxDatabases = user.maxDatabases ?? 0;
  selectedUserForm.maxMailboxes = user.maxMailboxes ?? 0;
  selectedUserForm.maxScheduledTasks = user.maxScheduledTasks ?? 0;
  selectedUserForm.parentUserId = user.parentUserId ?? "";
  selectedUserForm.preferredLanguage = user.preferredLanguage;
  selectedUserForm.isActive = user.isActive;
}

async function saveUserChanges() {
  if (!selectedUserId.value) {
    return;
  }

  try {
    await authStore.updateUser(selectedUserId.value, { ...selectedUserForm, ownerId: selectedUserForm.ownerId || null });
    if (selectedUserId.value === authStore.session?.user?.id) {
      await authStore.fetchSession();
    }
    notify(t("panel.users.saved"), false);
  } catch (error) {
    notify(resolveApiMessage(error), true);
  }
}

function notify(message: string, isError: boolean) {
  emit("notify", message, isError);
}

function resolveApiMessage(error: unknown): string {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}
</script>

<style scoped>
/*
 * BU DOSYADAN SILINEN KOPYALAR (hepsi base.css'te zaten var, olculmus gorunen etkileriyle):
 *   - `label:not(:where(...)) { display: grid }`  -> base `.form-grid label...` + `.stacked-field`
 *     ayni isi yapiyor; bu gorunumdeki HER label uc siniftan birini (stacked-field / check-field /
 *     checkbox) tasiyor, yani kural bir sey EKLEMIYORDU.
 *   - `input/select/textarea { width: 100% }`     -> base.css satir 724'te BIREBIR ayni kural.
 *   - `.checkbox`, `.checkbox input/span`         -> base.css'te tek kaynak olarak duruyor.
 *   - `.wide-button`                              -> sablonda HIC kullanilmiyordu (olu kural).
 *   - `.form-submit-cell button { width: auto }`  -> hicbir kural o dugmeyi germiyordu (olu).
 *   - `.plans-head`                               -> ortak `.section-head`; kazanc `flex-wrap`.
 *   - `.settings-wide-field`                      -> ortak `.form-note`ta birlesti.
 */

.users-workspace {
  align-content: start;
}

/* Atanan siteler: uzun liste formu itmesin diye KENDI ICINDE kayar. 180px olculmus bir
   sinir — 26px'lik satirdan ~6 tanesi gorunur, yani kaydirilacagi belli olur ama tipik bir
   musterinin sitelerini tek bakista gosterir. */
.customer-sites {
  grid-column: 1 / -1;
  display: grid;
  gap: var(--sp-6);
  max-height: 180px;
  overflow-y: auto;
  padding: var(--sp-10);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
}

/* KUTU-ETIKET ARASI TEK KAYNAKTAN. Kapsamli kopya burayi `inline-flex; gap: 8px` yapiyordu,
   ortak `.checkbox span` kurali ise ayrica 8px kenar bosluk veriyor: OLCULDU, arada 16px
   olusuyordu — ayni formdaki `.check-field` satirlarinin (8px) tam iki kati. Izgara araligi
   sifirlanir, boslugu tek bir kural (ortak `.checkbox span`) belirler. */
.customer-sites .checkbox {
  gap: 0;
}

/* SATIR TABANI 720 -> 860px. OLCULDU: ad sutunu 220 + uc durum sutunu ~3x90 + eylem seridi
   ~320px (Kilidi ac 80 · Giris yap 85 · Duzenle 75 · Sil 50 + aralikar) + 4x12 aralik = 858px.
   Ortak 720px tabaniyla eylem dugmeleri iki-uc satira sariyor, satir yuksekligi satirdan
   satira degisiyor ve tablo "birbirine girmis" gorunuyordu. Tablo (.data-table) yatay kayar
   ve BASLIK SATIRI ayni kaydirmanin icinde oldugu icin dar ekranda kaybolmaz. */
.user-row {
  grid-template-columns: minmax(220px, 1.5fr) 0.75fr 0.7fr 0.7fr auto;
  min-width: 860px;
}

.user-status-cell {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-4);
  min-width: 0;
}

/* EYLEM SERIDI IKI BOLGE: okuma/duzenleme solda, YIKICI sagda ve AYRI.
   Ayrac + sabit taban genislik, "Sil"i her satirda ayni sutunda tutar (bkz. sablondaki not).
   68px olculdu: en uzun etiket "Delete" (13.5px/600 ~ 44px) + 2 x --sp-8 dolgu + 8px pay. */
.user-row-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--sp-8);
}

.row-tools {
  display: inline-flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: var(--sp-6);
}

.row-danger {
  display: flex;
  justify-content: flex-end;
  min-width: 68px;
  padding-left: var(--sp-8);
  border-left: 1px solid var(--panel-border);
}

.plans-card {
  display: grid;
  gap: var(--sp-12);
}

.plans-grid {
  /* 190px, plan adı + "Sil" düğmesinin YAN YANA sığması için dardı: ad üç satıra bölünüyor ve
     kart bozuk görünüyordu. 240px, tipik plan adlarını tek satırda tutuyor. */
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: var(--sp-10);
}

.plan-card {
  border: 1px solid var(--panel-border);
  /* IC kart, kapsayicisinin (`.form-surface`, --radius-md = 9px) yariCapindan BUYUK olamaz;
     12px o iliskiyi ters ceviriyordu (dista 9, icte 12). */
  border-radius: var(--radius-md);
  background: var(--surface-1);
  padding: var(--sp-12);
  display: grid;
  gap: var(--sp-10);
  /* Kartlar EŞIT YUKSEKLIGE GERILMEZ: icerik ustten dizilir. */
  align-content: start;
}

.plan-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-8);
}

/* Plan adi artik `<h4>`: boyu ortak baslik merdiveninden gelir (--fs-h4, 14.3px). Onceden
   0.98rem'lik bir `<strong>` idi, yani kart basligi govde metniyle (13.5px) neredeyse ayni
   boydaydi ve kartin ici duz bir metin yigini gibi okunuyordu. */
.plan-card-head .plan-name {
  min-width: 0;
  /* `anywhere` DEĞİL: "E2E Başlangıç" adını "Baslang" / "ic" diye kelime ortasından bölüyordu ve
     plan adı bozuk görünüyordu. `break-word` yalnızca kelimenin kendisi satıra sığmadığında böler. */
  overflow-wrap: break-word;
}

/* Düğmeler ne büyür ne küçülür: adın uzunluğu değiştikçe kartlar arasında yerleri kaymasın. */
.plan-card-actions {
  display: flex;
  flex: none;
  gap: var(--sp-6);
}

.plan-card-head button {
  flex: none;
  white-space: nowrap;
}

.plan-limits {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--sp-6);
}

.plan-limits li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-10);
  font-size: var(--fs-sm);
  padding: var(--sp-4) 0;
  border-top: 1px solid color-mix(in srgb, var(--panel-border) 60%, transparent);
}

.plan-limits li:first-child {
  border-top: none;
}

.plan-limits li span {
  color: var(--text-muted);
}

/* Deger SAGDA (space-between) ve TABULAR: kartlar yan yana dizildigi icin "1024" ile "128"
   alt alta dustugunde basamaklarin ayni sutuna oturmasi karsilastirmayi mumkun kilar. */
.plan-limits li b {
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}

.plans-empty {
  padding: var(--sp-16) 0;
}

.plan-form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: var(--sp-12);
  /* USTTEN hizala. "end" iken, altinda ipuc yazisi olan hucreler (aylik trafik, gecerlilik)
     digerlerinden uzun oldugu icin girdileri yukari kaciyor ve etiketler kademeli duruyordu. */
  align-items: start;
  padding-top: var(--sp-12);
  border-top: 1px solid var(--panel-border);
}

.plan-form-grid .plan-name-cell {
  grid-column: 1 / -1;
}

/* IZGARANIN TAM GENISLIGINI KAPLAYAN NOT ve EYLEM SATIRI — tek kaynak.
   Onceden ayni isi yapan DORT ad vardi: `.plan-form-hint`, `.quota-hint` (ikisi de birebir
   `grid-column: 1/-1; margin: 0; muted; 0.8rem`), `.plan-form-actions` ve `.form-submit-cell`.
   Metin bicimi artik ortak `.hint`ten gelir; buradaki tek is sutun genisligi. */
.form-note {
  grid-column: 1 / -1;
  margin: 0;
}

.form-actions {
  grid-column: 1 / -1;
  display: flex;
  justify-content: flex-end;
  gap: var(--sp-8);
}

/* Kota okumalari ("512 MiB / 5 GiB") satirlar boyunca alt alta karsilastiriliyor: basamaklar
   esit genislikte olmali, yoksa iki satirdaki sayilar hizasiz gorunur. */
.customer-meta {
  color: var(--accent-text);
  font-variant-numeric: tabular-nums;
}
</style>
