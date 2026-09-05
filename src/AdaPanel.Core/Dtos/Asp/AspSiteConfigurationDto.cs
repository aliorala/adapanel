namespace AdaPanel.Core.Dtos.Asp;

public sealed class AspSiteConfigurationDto
{
    public string SiteName { get; init; } = string.Empty;

    public string ApplicationPoolName { get; init; } = string.Empty;

    public bool ClassicAspEnabled { get; init; }

    public string ManagedRuntimeVersion { get; init; } = string.Empty;

    public string ManagedPipelineMode { get; init; } = "Integrated";

    public bool Enable32BitAppOnWin64 { get; init; }

    public bool HasManagedRuntime => !string.IsNullOrWhiteSpace(ManagedRuntimeVersion);

    public bool HasPhpHandler { get; init; }

    public string PhpComponentId { get; init; } = string.Empty;

    public string PhpHandlerName { get; init; } = string.Empty;

    public bool IsMixedHandlersEnabled { get; init; }

    /// <summary>
    /// PHP handler'i commit'ten SONRA gercekten StaticFile'dan once mi? UC DEGERLI.
    /// Varsayilan: OLCULMEDI -- yani "sirali" varsayilmaz.
    /// </summary>
    public HandlerOrderMeasurement PhpHandlerOrder { get; init; } = HandlerOrderMeasurement.Olculemedi();

    /// <summary>Classic ASP handler'i icin ayni olcum.</summary>
    public HandlerOrderMeasurement ClassicAspHandlerOrder { get; init; } = HandlerOrderMeasurement.Olculemedi();

    /// <summary>
    /// Kullaniciya gosterilecek uyari anahtari; her sey OLCULDU ve yolundaysa bos doner.
    /// Ilgisiz olcumler (ASP kapaliysa ASP sirasi, PHP handler'i yoksa PHP sirasi) DISLANIR --
    /// aksi halde panel var olmayan bir sorun icin surekli uyarirdi.
    /// </summary>
    public string HandlerOrderNoticeKey
    {
        get
        {
            if (HasPhpHandler && PhpHandlerOrder.NoticeKey.Length > 0)
            {
                return PhpHandlerOrder.NoticeKey;
            }

            return ClassicAspEnabled ? ClassicAspHandlerOrder.NoticeKey : string.Empty;
        }
    }

    /// <summary>Bir kopya uzerinde YALNIZ olcum alanlarini degistirir (init-only alanlar icin).</summary>
    public AspSiteConfigurationDto WithHandlerOrder(
        HandlerOrderMeasurement? php = null,
        HandlerOrderMeasurement? classicAsp = null)
        => new()
        {
            SiteName = SiteName,
            ApplicationPoolName = ApplicationPoolName,
            ClassicAspEnabled = ClassicAspEnabled,
            ManagedRuntimeVersion = ManagedRuntimeVersion,
            ManagedPipelineMode = ManagedPipelineMode,
            Enable32BitAppOnWin64 = Enable32BitAppOnWin64,
            HasPhpHandler = HasPhpHandler,
            PhpComponentId = PhpComponentId,
            PhpHandlerName = PhpHandlerName,
            IsMixedHandlersEnabled = IsMixedHandlersEnabled,
            PhpHandlerOrder = php ?? PhpHandlerOrder,
            ClassicAspHandlerOrder = classicAsp ?? ClassicAspHandlerOrder
        };
}
