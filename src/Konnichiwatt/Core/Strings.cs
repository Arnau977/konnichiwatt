using System.Globalization;

namespace Konnichiwatt.Core;

/// <summary>
/// User-facing texts in Spanish and English. The language follows Windows (Spanish if the UI language is Spanish,
/// English otherwise) unless the user picks one in Settings › General.
/// </summary>
internal static class L
{
    /// <summary>"es" or "en".</summary>
    public static string Language { get; private set; } = Resolve("auto");

    /// <param name="setting">"auto", "es" or "en".</param>
    public static void Init(string? setting) => Language = Resolve(setting);

    private static string Resolve(string? setting) => setting switch
    {
        "es" => "es",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en",
    };

    private static bool Es => Language == "es";
    private static string T(string es, string en) => Es ? es : en;

    // ---- Tray, menu, notifications ----------------------------------------------------
    public static string NoDevicesDetected => T("Ningún dispositivo detectado", "No devices detected");
    public static string NoVisibleDevices => T("Ningún dispositivo visible", "No visible devices");
    public static string RefreshNow => T("Actualizar ahora", "Refresh now");
    public static string SettingsMenu => T("Ajustes…", "Settings…");
    public static string Exit => T("Salir", "Exit");
    public static string AsleepShort => T("reposo", "asleep");
    public static string LowBatteryTitle => T("Batería baja", "Low battery");
    public static string ChargeCompleteTitle => T("Carga completa", "Fully charged");
    public static string ChargeCompleteBody(string name) =>
        T($"{name} está al 100 %. Ya puedes desconectarlo.", $"{name} is at 100 %. You can unplug it now.");
    public static string TestNotificationBody =>
        T("Así se verán los avisos de batería baja y de carga completa.", "This is how low-battery and full-charge alerts will look.");

    // ---- Device kinds and states --------------------------------------------------------
    public static string Headset => T("Auriculares", "Headset");
    public static string Mouse => T("Ratón", "Mouse");
    public static string Keyboard => T("Teclado", "Keyboard");
    public static string Controller => T("Mando", "Controller");
    public static string Speaker => T("Altavoz", "Speaker");
    public static string Device => T("Dispositivo", "Device");
    public static string Other => T("Otro", "Other");
    public static string Asleep => T("En reposo", "Asleep");
    public static string AsleepMinutesAgo(int m) => T($"En reposo · dato de hace {m} min", $"Asleep · reading from {m} min ago");
    public static string AsleepHoursAgo(int h) => T($"En reposo · dato de hace {h} h", $"Asleep · reading from {h} h ago");
    public static string Charging => T("Cargando", "Charging");
    public static string Charged => T("Cargado", "Charged");
    public static string OnBattery => T("Con batería", "On battery");
    public static string Connected => T("Conectado", "Connected");
    public static string Minutes(int n) => $"{n} min";
    public static string Hours(double n) => $"{n} h";
    public static string Days(double n) => T($"{n} días", $"{n} days");
    public static string TimeLeft(string amount) => T($"≈ {amount} restantes", $"≈ {amount} left");
    public static string FullIn(string amount) => T($"lleno en ≈ {amount}", $"full in ≈ {amount}");
    public static string ScreenN(int n) => T($"Pantalla {n}", $"Display {n}");
    public static string XboxController => T("Mando Xbox", "Xbox controller");
    public static string GameSirController => T("Mando GameSir", "GameSir controller");
    public static string Ds4Adapter => T("DualShock 4 (adaptador)", "DualShock 4 (adapter)");

    // ---- Settings window ----------------------------------------------------------------
    public static string SettingsTitle => T("Ajustes de Konnichiwatt", "Konnichiwatt settings");
    public static string Tagline => "konnichiwa + watt";
    public static string PageDevices => T("Dispositivos", "Devices");
    public static string PageAppearance => T("Apariencia", "Appearance");
    public static string PageAlerts => T("Avisos", "Alerts");
    public static string PageGeneral => T("General", "General");

    public static string NoDevicesHint =>
        T("Enciende tus dispositivos inalámbricos; aparecerán aquí en unos segundos.", "Turn on your wireless devices; they'll show up here in a few seconds.");
    public static string Options => T("Opciones", "Options");
    public static string HideWhileCharging => T("Ocultar los que se están cargando", "Hide devices while they charge");
    public static string HideWhileChargingDesc => T("Mientras un dispositivo carga no ocupa sitio en la barra", "A charging device takes no space on the taskbar");
    public static string Reset => T("Restablecer", "Reset");
    public static string OriginalOrder => T("Orden original", "Original order");
    public static string OriginalOrderDesc => T("Vuelve al orden en que se detectan los dispositivos", "Go back to the order devices are detected in");
    public static string LowBatteryAlert => T("Aviso de batería baja", "Low battery alert");
    public static string GeneralThreshold(int t) => T($"General ({t} %)", $"General ({t} %)");
    public static string AtPercent(int t) => T($"Al {t} %", $"At {t} %");
    public static string Automatic(string what) => T($"Automático ({what})", $"Automatic ({what})");
    public static string AlertAt(int t) => T($"aviso al {t} %", $"alert at {t} %");
    public static string NotConnectedHidden => T("No conectado · oculto", "Not connected · hidden");
    public static string Preview => T("Vista previa", "Preview");

    public static string[] DisplayModes => Es ? new[] { "Porcentaje", "Icono", "Ambos" } : new[] { "Percent", "Icon", "Both" };
    public static string ShowBatteryAs => T("Mostrar batería como", "Show battery as");
    public static string ShowBatteryAsDesc => T("Número, batería o ambos", "Number, battery or both");
    public static string BoldText => T("Texto e iconos gruesos", "Bold text and icons");
    public static string BoldTextDesc => T("Más legible en monitores con escala al 100 %", "Easier to read on displays at 100 % scale");
    public static string DiscreetMode => T("Modo discreto", "Discreet mode");
    public static string DiscreetModeDesc => T("En la barra solo aparecen los que tienen poca batería", "Only devices running low appear on the taskbar");
    public static string ShowWhenBelow => T("Mostrar cuando bajen del", "Show when below");
    public static string ShowWhenBelowDesc => T("La bandeja los sigue mostrando todos", "The tray icon still covers all of them");
    public static string WhereShown => T("Dónde se muestra", "Where it shows");
    public static string ShowOnTaskbar => T("Mostrar en la barra de tareas", "Show on the taskbar");
    public static string ShowOnTaskbarDesc => T("Si lo desactivas, solo queda el icono de la bandeja", "If off, only the tray icon remains");
    public static string ScreenNumber(int n) => T($"pantalla {n}", $"display {n}");
    public static string Primary => T("Principal", "Main");
    public static string OnlyScreen => T("única pantalla", "only display");
    public static string NoTaskbarOnScreen => T("Sin barra de tareas (actívala en los ajustes de Windows)", "No taskbar here (turn it on in Windows settings)");
    public static string ToTheLeft => T("a la izquierda", "on the left");
    public static string ToTheRight => T("a la derecha", "on the right");
    public static string Above => T("arriba", "above");
    public static string Below => T("abajo", "below");
    public static string Overlapping => T("superpuesta", "overlapping");

    public static string LowBatteryAlertDesc => T("Una notificación cuando un dispositivo se está quedando sin batería", "A notification when a device is running low");
    public static string AlertWhenBelow => T("Avisar al bajar del", "Alert below");
    public static string AlertWhenBelowDesc => T("General; cada dispositivo puede tener el suyo (⋯)", "Devices can override it (⋯)");
    public static string FullChargeAlert => T("Aviso de carga completa", "Full charge alert");
    public static string FullChargeAlertDesc => T("Cuando un dispositivo que estaba cargando llega al 100 %", "When a charging device reaches 100 %");
    public static string SendTest => T("Enviar aviso de prueba", "Send a test alert");
    public static string Check => T("Comprobar", "Check");
    public static string TestNotifications => T("Probar las notificaciones", "Test notifications");
    public static string TestNotificationsDesc => T("Si no aparece, revisa «No molestar»", "If nothing shows, check Do Not Disturb");

    public static string StartWithWindows => T("Iniciar con Windows", "Start with Windows");
    public static string StartWithWindowsDesc => T("Konnichiwatt se abre solo al iniciar sesión", "Konnichiwatt opens when you sign in");
    public static string PollFrequency => T("Frecuencia de lectura", "Reading frequency");
    public static string PollFrequencyDesc => T("Al conectar uno, al momento", "New devices are read right away");
    public static string ReadNow => T("Leer la batería ahora", "Read batteries now");
    public static string Language_ => T("Idioma", "Language");
    public static string LanguageDesc => T("Automático = el de Windows", "Automatic = Windows'");
    public static string[] LanguageOptions => new[] { T("Automático", "Automatic"), "Español", "English" };
    public static string Help => T("Ayuda", "Help");
    public static string Diagnostics => T("Diagnóstico", "Diagnostics");
    public static string DiagnosticsDesc => T("Útil si un dispositivo no aparece", "Useful if a device doesn't show up");
    public static string GenerateReport => T("Generar informe", "Create report");
    public static string LogsFolder => T("Carpeta de registros", "Logs folder");
    public static string Open => T("Abrir", "Open");
    public static string Footer(string version) =>
        T($"Konnichiwatt {version} · konnichiwa (こんにちは, hola) + watt", $"Konnichiwatt {version} · konnichiwa (こんにちは, hello) + watt");

    // ---- Rename dialog ----------------------------------------------------------------
    public static string RenameTitle => T("Cambiar nombre", "Rename");
    public static string NameFor(string original) => T($"Nombre para «{original}»:", $"Name for “{original}”:");
    public static string Ok => T("Aceptar", "OK");
    public static string Cancel => T("Cancelar", "Cancel");
    public static string OriginalName => T("Nombre original", "Original name");

    // ---- Updates ------------------------------------------------------------------------
    public static string Updates => T("Actualizaciones", "Updates");
}
