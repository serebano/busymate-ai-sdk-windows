using System;
using System.Threading.Tasks;
using System.Windows;

namespace Busymate.AI.Example;

public partial class MainWindow : Window
{
    private const int MaxLog = 6000;

    public MainWindow()
    {
        InitializeComponent();
        Display(DemoSession.Settings);
        Chat.StartFailed += (_, error) =>
            Log("WebView2 could not start (" + error.GetType().Name + "). Install the WebView2 Runtime and Apply again.");
        Loaded += async (_, _) => await StartChatAsync();
        Closed += (_, _) => Chat.Dispose();
        Log("Guest demo uses the real hosted chat. Tokens, nonces, account ids and audio are never logged.");
    }

    private void Display(DemoSettings s)
    {
        AssistantBox.Text = s.Assistant;
        ChatUrlBox.Text = s.ChatUrl;
        LocaleBox.Text = s.Locale;
        OriginsBox.Text = s.ExtraOrigins;
        GuestRadio.IsChecked = !s.IdentityEnabled;
        IdentityRadio.IsChecked = s.IdentityEnabled;
        BackendUrlBox.Text = s.BackendUrl;
        AccountIdBox.Text = s.AccountId;
        BearerBox.Password = s.BackendBearer;
        MicrophoneCheck.IsChecked = s.MicrophoneEnabled;
        ConfirmCheck.IsChecked = s.ConfirmMicrophone;
        CloseCheck.IsChecked = s.CloseEnabled;
        ActionsCheck.IsChecked = s.ActionsEnabled;
        RestrictCheck.IsChecked = s.RestrictNavigation;
    }

    private DemoSettings Read() => new()
    {
        Assistant = AssistantBox.Text.Trim(),
        ChatUrl = ChatUrlBox.Text.Trim(),
        Locale = LocaleBox.Text.Trim(),
        ExtraOrigins = OriginsBox.Text.Trim(),
        IdentityEnabled = IdentityRadio.IsChecked == true,
        BackendUrl = BackendUrlBox.Text.Trim(),
        AccountId = AccountIdBox.Text.Trim(),
        BackendBearer = BearerBox.Password,
        MicrophoneEnabled = MicrophoneCheck.IsChecked == true,
        ConfirmMicrophone = ConfirmCheck.IsChecked == true,
        CloseEnabled = CloseCheck.IsChecked == true,
        ActionsEnabled = ActionsCheck.IsChecked == true,
        RestrictNavigation = RestrictCheck.IsChecked == true,
    };

    private async Task StartChatAsync()
    {
        var settings = DemoSession.Settings;
        ClosedPanel.Visibility = Visibility.Collapsed;
        Chat.Visibility = Visibility.Visible;
        var options = settings.ChatOptions(
            mint: (request, cancellation) =>
            {
                Log("Backend mint requested (sensitive fields omitted).");
                return BackendMint.MintAsync(settings, DemoSession.SignedIn, request, cancellation);
            },
            log: Log,
            close: CloseChat,
            confirmMicrophone: ConfirmMicrophoneAsync);
        await Chat.StartAsync(settings.EffectiveChatUrl(), options);
        Log("Chat started: " + (settings.IdentityEnabled ? "identity bridge" : "guest") +
            ", microphone " + (settings.MicrophoneEnabled ? "on" : "off") + ".");
    }

    private Task<bool> ConfirmMicrophoneAsync(string origin, string source)
    {
        var answer = MessageBox.Show(this,
            "Allow the support chat (" + origin + ") to use your microphone for " + (source == "voice" ? "voice mode" : "dictation") + "?",
            "Microphone", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return Task.FromResult(answer == MessageBoxResult.Yes);
    }

    private void CloseChat()
    {
        Dispatcher.Invoke(() =>
        {
            Chat.Visibility = Visibility.Collapsed;
            ClosedPanel.Visibility = Visibility.Visible;
        });
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var updated = Read();
        var error = updated.ValidationError();
        if (error is not null)
        {
            MessageBox.Show(this, error, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DemoSession.Settings = updated;
        Log("Settings applied; recreating the WebView.");
        await StartChatAsync();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var s = DemoSession.Settings;
        if (!s.IdentityEnabled || s.BackendUrl.Length == 0 || s.AccountId.Length == 0)
        {
            Log("Choose identity mode, configure the backend URL and account ID, then Apply first.");
            return;
        }
        DemoSession.SignedIn = true;
        Chat.Chat?.AccountChanged();
        Log("Account changed; the chat will ask your authenticated backend.");
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        DemoSession.SignedIn = false;
        Chat.Chat?.AccountChanged();
        Log("Signed out: state event sent.");
    }

    private void AccountChanged_Click(object sender, RoutedEventArgs e)
    {
        Chat.Chat?.AccountChanged();
        Log("Current account state emitted.");
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        Chat.Reload();
        Log("Chat reload requested.");
    }

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowsMicrophonePrivacy.OpenSettings();
            Log("Opened Windows microphone privacy settings (current state: " + WindowsMicrophonePrivacy.Read() + ").");
        }
        catch (Exception)
        {
            Log("Could not open Windows Settings.");
        }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        DemoSession.Settings = new DemoSettings();
        DemoSession.SignedIn = false;
        Display(DemoSession.Settings);
        Log("Demo reset to guest defaults.");
        await StartChatAsync();
    }

    private async void Reopen_Click(object sender, RoutedEventArgs e) => await StartChatAsync();

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Log(message)));
            return;
        }
        LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        if (LogBox.Text.Length > MaxLog) LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length - 4000);
        LogBox.ScrollToEnd();
    }
}
