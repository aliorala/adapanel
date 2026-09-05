using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Asp;

namespace AdaPanel.Infrastructure.AspNet;

/// <summary>
/// IIS YAPILANDIRMASINA ERİŞİMİN TEK KAYNAĞI (<c>Microsoft.Web.Administration</c>, yansımayla).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden yansıma, doğrudan başvuru değil:</b> <c>Microsoft.Web.Administration.dll</c> NuGet'ten
/// değil <c>%windir%\System32\inetsrv</c>'den gelir ve IIS kurulu olmayan makinede HİÇ YOKTUR.
/// Derleme zamanı başvurusu, panelin IIS'siz bir makinede (geliştirme, birim testi, kurulum öncesi)
/// AÇILAMAMASI demek olurdu. Yükleme çalışma zamanında yapılır ve dosya yoksa
/// <c>sites.iis_not_available</c> ile GÜRÜLTÜLÜ biter — sessiz "her şey yolunda" dalı yok.
/// </para>
/// <para>
/// <b>Neden ayrı bir dosyaya çıkarıldı:</b> bu yardımcıların tamamı <see cref="AspNetService"/>
/// içinde private static olarak yaşıyordu. <c>.htaccess</c> modülünün IIS kaydını ÖLÇMEK için
/// aynı erişim gerekti ve iki seçenek vardı: kopyalamak ya da tek kaynağa çıkarmak. Kopya,
/// bu depoda bedeli defalarca ödenmiş hata sınıfıdır (iki uygulama zamanla ayrışır; birinde
/// düzeltilen bir hata diğerinde yaşamaya devam eder). <see cref="AspNetService"/> artık
/// <c>using static</c> ile buradan tüketiyor, yani çağrı yerleri değişmedi.
/// </para>
/// </remarks>
internal static class IisConfigurationAccess
{
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";

    /// <summary>
    /// <c>ServerManager</c> örneği. IIS yoksa <c>sites.iis_not_available</c> (503) fırlatır.
    /// </summary>
    internal static IDisposable CreateServerManager()
    {
        var assemblyPath = Environment.ExpandEnvironmentVariables(WebAdministrationAssemblyPath);
        if (!File.Exists(assemblyPath))
        {
            throw new AppException("sites.iis_not_available", 503, 503);
        }

        var assembly = Assembly.LoadFrom(assemblyPath);
        var serverManagerType = assembly.GetType(ServerManagerTypeName)
            ?? throw new AppException("sites.iis_not_available", 503, 503);

        return Activator.CreateInstance(serverManagerType) as IDisposable
            ?? throw new AppException("sites.iis_not_available", 503, 503);
    }

    internal static IEnumerable<object> Enumerate(object collection)
    {
        return collection is IEnumerable enumerable
            ? enumerable.Cast<object>()
            : [];
    }

    /// <summary>
    /// Sitenin <c>system.webServer/handlers</c> koleksiyonu.
    /// </summary>
    internal static object GetHandlersCollection(IDisposable serverManager, string siteName)
    {
        var configuration = InvokeMethod(serverManager, "GetWebConfiguration", siteName);
        var handlersSection = InvokeMethod(configuration, "GetSection", "system.webServer/handlers");
        return GetConfigurationCollection(handlersSection);
    }

    /// <summary>
    /// Handler koleksiyonunu SIRASIYLA okur. Olcumun ham girdisi budur.
    /// </summary>
    internal static IReadOnlyList<HandlerSnapshot> SnapshotHandlers(object handlers)
        => Enumerate(handlers)
            .Select(item => new HandlerSnapshot(TryGetAttributeString(item, "name"), TryGetAttributeString(item, "path")))
            .ToArray();

    /// <summary>
    /// Handler'i koleksiyonun BASINA koyar; koyamazsa SONA ekler ve BUNU SOYLER.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>TEK KAYNAK.</b> Ayni dort satir <c>AspNetService</c> (PHP handler'i, Classic ASP handler'i)
    /// ve <c>PhpService</c> (PHP handler'i) icinde UC KEZ kopyalanmisti; ucunde de fallback CIPLAK
    /// bir <c>catch</c> ile sonu getiriyordu.
    /// </para>
    /// <para>
    /// <b>Fallback NEDEN duruyor.</b> Bazi IIS yapilandirmalarinda koleksiyonun 0. indeksi kilitli
    /// olabilir; orada firlatmak, handler'i HIC yazmamak demek olurdu. Ama sona eklemek
    /// <c>404.3</c> URETEN siralamadir; bu yuzden donus degeri KISMI BASARIYI tasir ve cagiran
    /// commit'ten sonra <see cref="HandlerOrderMeasurement"/> ile GERCEK sirayi olcer.
    /// Yani fallback artik "sessiz basari" degil, ISARETLENMIS bir tavizdir.
    /// </para>
    /// </remarks>
    internal static bool InsertHandlerFirst(object handlers, object handler)
    {
        try
        {
            InvokeMethod(handlers, "AddAt", 0, handler);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            InvokeMethod(handlers, "Add", handler);
            return false;
        }
    }

    internal static object GetPropertyObject(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            return property.GetValue(target)
                ?? throw new AppException("aspnet.configuration_failed", 500, 500);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    internal static object GetConfigurationCollection(object section)
    {
        var property = section.GetType().GetProperty("Collection");
        if (property is not null)
        {
            try
            {
                var collection = property.GetValue(section);
                if (collection is not null)
                {
                    return collection;
                }
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        return InvokeMethod(section, "GetCollection");
    }

    internal static string GetStringProperty(object target, string propertyName)
    {
        return GetPropertyObject(target, propertyName).ToString() ?? string.Empty;
    }

    internal static bool GetBoolProperty(object target, string propertyName)
    {
        return Convert.ToBoolean(GetPropertyObject(target, propertyName));
    }

    internal static string GetAttributeString(object target, string attributeName)
    {
        return InvokeMethod(target, "GetAttributeValue", attributeName).ToString() ?? string.Empty;
    }

    /// <summary>
    /// Bulunmayan/okunamayan öznitelik için boş dize döner.
    /// </summary>
    /// <remarks>
    /// Yutma BURADA meşru: bir özniteliğin YOKLUĞU da bir ölçümdür (örneğin <c>preCondition</c>
    /// yazılmamışsa modül her istekte koşar) ve istisna fırlatmak o ölçümü "okunamadı"ya
    /// çevirirdi. Çağıranın "boş" ile "okunamadı"yı ayırması gerekiyorsa bunu bölüm/koleksiyon
    /// düzeyinde yapmalı — orada istisna YUTULMUYOR.
    /// </remarks>
    internal static string TryGetAttributeString(object target, string attributeName)
    {
        try
        {
            return GetAttributeString(target, attributeName);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static void SetAttributeValue(object target, string attributeName, object? value)
    {
        InvokeMethod(target, "SetAttributeValue", attributeName, value);
    }

    internal static void SetPropertyValue(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            if (value is null)
            {
                property.SetValue(target, null);
                return;
            }

            var propertyType = property.PropertyType;
            var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

            if (targetType.IsEnum)
            {
                var enumValue = value is string text
                    ? Enum.Parse(targetType, text, ignoreCase: true)
                    : Enum.ToObject(targetType, value);

                property.SetValue(target, enumValue);
                return;
            }

            if (targetType == typeof(string))
            {
                property.SetValue(target, value.ToString());
                return;
            }

            var converted = Convert.ChangeType(value, targetType);
            property.SetValue(target, converted);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    internal static object InvokeMethod(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length == arguments.Length &&
                AreParametersCompatible(candidate.GetParameters(), arguments));

        if (method is null)
        {
            throw new MissingMethodException(target.GetType().FullName, methodName);
        }

        try
        {
            return method.Invoke(target, arguments)
                ?? new object();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static bool AreParametersCompatible(ParameterInfo[] parameters, object?[] arguments)
    {
        for (var index = 0; index < parameters.Length; index += 1)
        {
            var parameterType = parameters[index].ParameterType;
            var argument = arguments[index];

            if (argument is null)
            {
                if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null)
                {
                    return false;
                }

                continue;
            }

            if (parameterType.IsInstanceOfType(argument))
            {
                continue;
            }

            if (parameterType.IsEnum && argument is IConvertible)
            {
                continue;
            }

            if (typeof(IConvertible).IsAssignableFrom(parameterType) && argument is IConvertible)
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
