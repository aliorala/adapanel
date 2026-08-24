using System;

// ============================================================================================
// DERLEYICI ISKELESI &#8212; net48'de EKSIK OLAN "sozde tipler".
// ============================================================================================
//
// NEDEN VAR: C# derleyicisi bazi dil ozelliklerini uretirken BCL'de belirli tiplerin var olmasini
// sart kosar. Tipler CLR icin degil, DERLEYICI icin gereklidir; hicbir calisma zamani davranisi
// tasimazlar. .NET (Core) bunlari kendi BCL'inde tasir, .NET Framework 4.8 TASIMAZ.
//
// Bu yuzden motorun kaynaklarini net48'e tasirken UC secenek vardi:
//   (a) `init` ve `required` kullanan HER uyeyi net48 icin yeniden yazmak. Motorun DTO'lari
//       bastan sona `init` ile yazilmis (286 derleme hatasi olculdu); yeniden yazmak degistirilmis
//       bir motor demekti &#8212; yani tam olarak kacinmak istedigimiz "iki kaynak" durumu. ELENDI.
//   (b) NuGet'ten bir polyfill paketi cekmek. Modul GAC'a kuruluyor ve GAC'taki bir derleme
//       uygulamanin /bin klasorunden bagimlilik COZEMEZ; ek bir derlemeyi de GAC'a kurmak
//       kurulumu ikinci bir artefakta bagimli yapardi. ELENDI.
//   (c) Tipleri BURADA tanimlamak. Uretilen IL'de yalniz oznitelik/isaret olarak gorunurler,
//       derlemeye HICBIR bagimlilik eklemezler. SECILDI.
//
// BU DOSYA net8 TARAFINA GITMEZ: yalniz bu projenin (net48) kendi dosyasidir. AdaPanel.Infrastructure
// ayni tipleri kendi BCL'inden alir; iki tanim carpismaz cunku ayri derlemelerdir ve bu tanimlar
// `internal`dir.
//
// KAYNAK: tanimlarin sekli dotnet/runtime'daki asillariyla AYNI olmak zorunda &#8212; derleyici
// ad alanini, adi ve kurucu imzasini ADIYLA arar. Farkli yazilsalardi derleyici onlari gormezdi.
// ============================================================================================

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// <c>init</c> erisimcilerinin isareti. Derleyici her <c>init</c> set-metodunun donus tipine
    /// bu tipi "modreq" olarak ekler; boylece ESKI bir derleyici o uyeyi normal bir setter sanip
    /// nesne kurulduktan SONRA yazamaz. Govdesi bilerek bostur.
    /// </summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>
    /// <c>required</c> uyelerin isareti. Derleyici tipin ve uyenin uzerine yazar; nesne
    /// baslatilirken zorunlu uyelerin atandigini DERLEME ZAMANINDA denetler.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property,
        AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>
    /// "Bu uyeyi anlamayan derleyici onu KULLANMASIN" isareti. <c>required</c> ile birlikte
    /// uretilir: eski bir derleyici zorunlu uyeleri denetleyemeyecegi icin tipi hic kullanamamalidir.
    /// </summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }

        public bool IsOptional { get; set; }
    }
}

namespace System
{
    /// <summary>
    /// <c>^1</c> / <c>0</c> gibi dizin ifadelerinin tipi (C# 8 "index from end").
    /// </summary>
    /// <remarks>
    /// <para>
    /// Motorun ayristiricisi ve kural kosucusu dilim yazimini (<c>metin[1..]</c>, <c>metin[..n]</c>)
    /// YOGUN kullaniyor: net48 derlemesinde 65 ayri satir bu iki tipi istedi. Ucu de olcuydu:
    /// </para>
    /// <list type="number">
    /// <item>65 satiri <c>Substring</c>'e cevirmek -> motorun EN riskli kodunda (dizin aritmetigi)
    ///   65 elle donusum; her biri bir kapali-araliktan-acik-araliga hatasi firsati. ELENDI.</item>
    /// <item><c>#if NET48</c> ile iki yazim -> ayni satirin iki surumu, ikisi bir gun ayri duser.
    ///   ELENDI.</item>
    /// <item>Tipleri burada tanimlamak -> KAYNAK HIC DEGISMEZ, iki hedefte de ayni ifade derlenir.
    ///   SECILDI.</item>
    /// </list>
    /// <para>
    /// <b>DAVRANIS AYNI:</b> <c>string</c> icin derleyici <c>Range</c>'i dogrudan
    /// <c>Substring(start, length)</c>'e cevirir, <c>Index</c>'i de <c>Length - deger</c>'e; yani
    /// net48 ciktisi net8 ciktisiyla ayni aritmetigi uretir. DIZI dilimleri ayri konudur
    /// (<c>RuntimeHelpers.GetSubArray</c> gerektirir, o BCL tipine uye eklenemez) -- oradaki birkac
    /// satir kaynakta ELLE tasinabilir yazildi.
    /// </para>
    /// <para>
    /// Sekil dotnet/runtime'daki asliyla AYNI olmak zorunda: derleyici <c>GetOffset</c>,
    /// <c>Value</c>, <c>IsFromEnd</c>, <c>Start</c>, <c>End</c> uyelerini ADIYLA cagirir.
    /// </para>
    /// </remarks>
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Dizin negatif olamaz.");
            }

            // ASLIYLA AYNI KODLAMA: sondan sayilan dizinler bitwise-not ile saklanir, boylece tek
            // bir int hem degeri hem yonu tasir.
            _value = fromEnd ? ~value : value;
        }

        private Index(int value) => _value = value;

        public static Index Start => new Index(0);

        public static Index End => new Index(~0);

        public static Index FromStart(int value) => new Index(value, false);

        public static Index FromEnd(int value) => new Index(value, true);

        public int Value => _value < 0 ? ~_value : _value;

        public bool IsFromEnd => _value < 0;

        /// <summary>Uzunlugu bilinen bir dizide gercek ofset. Derleyicinin cagirdigi uye budur.</summary>
        public int GetOffset(int length) => IsFromEnd ? length + _value + 1 : _value;

        public static implicit operator Index(int value) => FromStart(value);

        public bool Equals(Index other) => _value == other._value;

        public override bool Equals(object? obj) => obj is Index other && Equals(other);

        public override int GetHashCode() => _value;

        public override string ToString() => IsFromEnd ? "^" + Value.ToString() : Value.ToString();
    }

    /// <summary>
    /// <c>a..b</c> aralik ifadesinin tipi (C# 8). Bkz. <see cref="Index"/> gerekce yorumu.
    /// </summary>
    internal readonly struct Range : IEquatable<Range>
    {
        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public Index Start { get; }

        public Index End { get; }

        public static Range All => new Range(Index.Start, Index.End);

        public static Range StartAt(Index start) => new Range(start, Index.End);

        public static Range EndAt(Index end) => new Range(Index.Start, end);

        /// <summary>
        /// Derleyicinin dizi dilimlerinde cagirdigi uye. Sinir denetimi ASILDAKIYLE AYNI: gecersiz
        /// aralik <see cref="ArgumentOutOfRangeException"/> firlatir, sessizce kirpilmaz.
        /// </summary>
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            var start = Start.GetOffset(length);
            var end = End.GetOffset(length);

            if ((uint)end > (uint)length || (uint)start > (uint)end)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            return (start, end - start);
        }

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);

        public override bool Equals(object? obj) => obj is Range other && Equals(other);

        public override int GetHashCode() => (Start.GetHashCode() * 31) + End.GetHashCode();

        public override string ToString() => Start + ".." + End;
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// <c>required</c> uyeleri atlayan kuruculari isaretler. Kayit (record) tipleri icin derleyici
    /// bu tipi de arar.
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}
