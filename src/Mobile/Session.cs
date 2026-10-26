using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace ERPSystem.Mobile;

/// <summary>جلسة الجوال: التوكن والبريد وعنوان الخادم.</summary>
public static class Session
{
    private const string ServerUrlKey = "erp_server_url";
    private const string RememberMeKey = "erp_remember_me";
    private const string SavedEmailKey = "erp_saved_email";
    private const string SavedPasswordKey = "erp_saved_password";
    private const string LanguageKey = "erp_language";
    private const string CurrencyKey = "erp_currency";

    public static string Language
    {
        get => Preferences.Get(LanguageKey, "ar");
        set => Preferences.Set(LanguageKey, value);
    }

    public static string CurrencySymbol
    {
        get => Preferences.Get(CurrencyKey, "ر.س");
        set => Preferences.Set(CurrencyKey, value);
    }

    public static bool RememberMe
    {
        get => Preferences.Get(RememberMeKey, false);
        set => Preferences.Set(RememberMeKey, value);
    }

    public static string SavedEmail
    {
        get => Preferences.Get(SavedEmailKey, "");
        set => Preferences.Set(SavedEmailKey, value);
    }

    public static string SavedPassword
    {
        get => Preferences.Get(SavedPasswordKey, "");
        set => Preferences.Set(SavedPasswordKey, value);
    }

    // لا يوجد عنوان خادم افتراضي في حزمة العميل.
    // اضبط متغير البيئة ERP_API_URL، أو أدخل عنوان الخادم في شاشة الدخول
    // (يُحفظ في Preferences وله الأولوية). لا يُضمَّن عنوان أي خادم تطوير أو عميل.
    private const string DefaultTrialApiUrl = "";

    private static string DefaultBaseUrl =>
        Environment.GetEnvironmentVariable("ERP_API_URL")
        ?? (DeviceInfo.Platform == DevicePlatform.Android ? DefaultTrialApiUrl : "http://localhost:5187");

    public static string BaseUrl
    {
        get
        {
            try
            {
                var saved = Preferences.Get(ServerUrlKey, "");
                if (!string.IsNullOrWhiteSpace(saved)) return saved.Trim().TrimEnd('/');
            }
            catch { }
            return DefaultBaseUrl;
        }
        set
        {
            try
            {
                var v = (value ?? "").Trim().TrimEnd('/');
                if (string.IsNullOrWhiteSpace(v)) return;
                Preferences.Set(ServerUrlKey, v);
            }
            catch { }
        }
    }

    public static string Token = "";
    public static string UserEmail = "";
    public static string RolesCsv = "";

    public static void Logout()
    {
        Token = "";
        UserEmail = "";
    }
}