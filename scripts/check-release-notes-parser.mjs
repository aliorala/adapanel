// Surum notu ayristiricisinin denetimi -- GERCEK release-body.json ile calisir.
//
// NEDEN VAR: notlar panelde tek satira cokuyordu ve bu hic olculmuyordu; hatayi kullanici
// gordu. Ayristirma mantigi utils/releaseNotes.ts'te sunumdan ayri durdugu icin burada
// tarayicisiz olcebiliyoruz. Kullanim:  node scripts/check-release-notes-parser.mjs
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
const cikti = mkdtempSync(join(tmpdir(), "ada-notes-"));

let hata = 0;
const dogrula = (ad, kosul) => {
  console.log(`${kosul ? "  GECTI" : "  KALDI"}  ${ad}`);
  if (!kosul) hata++;
};

try {
  // TypeScript kaynagi dogrudan calistirilamaz; BIREBIR ayni dosya derlenip yuklenir.
  // Kopyalanmis bir taklit degil, uygulamanin kullandigi kodun kendisi olculur.
  // tsc, npx yerine DOGRUDAN node ile calistirilir: Node 24 Windows'ta .cmd sarmalayicilarini
  // kabuksuz calistirmayi reddediyor (EINVAL) ve kabuk acmak gereksiz bir degisken olurdu.
  const tsc = join(repo, "frontend", "node_modules", "typescript", "lib", "tsc.js");
  execFileSync(
    process.execPath,
    [tsc, "frontend/src/utils/releaseNotes.ts", "--outDir", cikti, "--module", "esnext", "--target", "es2020", "--moduleResolution", "bundler"],
    { cwd: repo, stdio: "inherit" },
  );

  const { ayristir } = await import(pathToFileURL(join(cikti, "releaseNotes.js")).href);

  const govde = JSON.parse(readFileSync(join(repo, "release-body.json"), "utf8")).body;

  console.log("\n1) Gercek surum notlari (release-body.json)");
  const bloklar = ayristir(govde);
  const say = (t) => bloklar.filter((b) => b.tur === t).length;
  console.log(`   ${bloklar.length} blok: ${["baslik", "paragraf", "liste", "kod", "alinti", "ayrac"].map((t) => `${t}=${say(t)}`).join(" ")}`);

  dogrula("baslik bulundu", say("baslik") > 0);
  dogrula("paragraf bulundu", say("paragraf") > 0);
  dogrula("kod blogu bulundu", say("kod") > 0);
  dogrula("ayrac bulundu", say("ayrac") > 0);
  dogrula("alinti bulundu", say("alinti") > 0);
  dogrula("tek bloga cokmedi", bloklar.length > 10);

  const kodBlok = bloklar.find((b) => b.tur === "kod");
  dogrula("kod blogunda satir sonlari KORUNDU", kodBlok.icerik.includes("\n"));
  dogrula("kod blogu citleri icerige sizmadi", !kodBlok.icerik.includes("```"));

  const kalinVar = bloklar.some((b) => b.parcalar.some((p) => p.tur === "kalin"));
  dogrula("kalin metin ayristirildi", kalinVar);
  const isaretKaldi = bloklar
    .filter((b) => b.tur !== "kod")
    .some((b) => b.parcalar.some((p) => p.tur === "metin" && (p.deger.includes("**") || p.deger.includes("##"))));
  dogrula("ham ** ve ## isareti KALMADI", !isaretKaldi);

  console.log("\n2) Guvenlik: metin hicbir zaman HTML'e cevrilmez");
  const kotu = ayristir('<img src=x onerror="alert(1)"> ve <script>alert(2)</' + "script>");
  const duzMetin = kotu.flatMap((b) => b.parcalar).map((p) => p.deger).join("");
  dogrula("HTML metin olarak KALDI (dugume cevrilmedi)", duzMetin.includes("<img") && duzMetin.includes("<script"));
  dogrula("ayristirici HTML uretmiyor", kotu.every((b) => typeof b.icerik === "string"));

  console.log("\n3) Bozuk girdide cokmez");
  for (const [ad, girdi] of [
    ["bos metin", ""],
    ["yalniz bosluk", "   \n\n  "],
    ["kapanmamis kod citi", "```\nkod satiri\nbitmedi"],
    ["kapanmamis kalin", "**yarim kalin"],
    ["kapanmamis kod isareti", "`yarim kod"],
    ["yalniz ayrac", "---"],
    ["cok derin baslik", "####### alti gecti"],
  ]) {
    let tamam = true;
    try {
      ayristir(girdi);
    } catch {
      tamam = false;
    }
    dogrula(ad, tamam);
  }

  console.log("\n4) Bicimlendirme dogru esleniyor");
  const liste = ayristir("- bir\n- iki\n- uc");
  dogrula("uc maddeli TEK liste", liste.length === 1 && liste[0].tur === "liste" && liste[0].ogeler.length === 3);
  const baslik = ayristir("## Basligim");
  dogrula("## -> seviye 2 baslik", baslik[0].tur === "baslik" && baslik[0].seviye === 2);
  const yapisik = ayristir("paragraf metni\n## Hemen ardindan baslik");
  dogrula("baslik paragrafa YAPISMADI", yapisik.length === 2 && yapisik[1].tur === "baslik");
} finally {
  rmSync(cikti, { recursive: true, force: true });
}

console.log(hata === 0 ? "\nTUMU GECTI" : `\n${hata} DENETIM KALDI`);
process.exit(hata === 0 ? 0 : 1);
