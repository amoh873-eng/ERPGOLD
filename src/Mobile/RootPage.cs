using System.Text;
using System.Globalization;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace ERPSystem.Mobile;

public class RootPage : ContentPage
{
    public static RootPage? Instance = null;

    private Entry _email = null!;
    private Entry _password = null!;
    private CheckBox _rememberMe = null!;
    private Entry _serverUrl = null!;
    private Label _status = null!;
    private ActivityIndicator _spin = null!;
    private VerticalStackLayout _itemsScroll = null!;
    private VerticalStackLayout _stmtScroll = null!;
    private Entry _search = null!;
    private Entry _invoiceItemSearch = null!;
    private Picker _itPicker = null!;
    private Picker _cuPicker = null!;
    private Picker _whPicker = null!;
    private Entry _qty = null!;
    private Entry _price = null!;
    private Label _linesLbl = null!;
    private Label _invoiceResult = null!;
    private Label _stmtResult = null!;

    private List<WarehouseDto> _whs = new();
    private List<CustomerDto> _custs = new();
    private List<SupplierDto> _sups = new();
    private List<ItemDto> _items = new();
    private List<ItemDto> _itemHits = new();
    private List<InvoiceLineDto> _lines = new();

    public RootPage()
    {
        Instance = this;
        FlowDirection = Session.Language == "en" ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
        BackgroundColor = Theme.Bg;
        _status = new Label { HorizontalTextAlignment = TextAlignment.Center, Margin = 10 };
        _spin = new ActivityIndicator { Color = Theme.Primary, IsRunning = false, IsVisible = false, HeightRequest = 40 };
        ShowLogin();
    }

    private void Show(View v)
    {
        MainThread.BeginInvokeOnMainThread(async () => {
            try
            {
                await Task.Delay(20);
                Content = v;
            }
            catch { }
        });
    }

    public void ShowLogin()
    {
        var root = new VerticalStackLayout { Spacing = 20, Padding = 30, VerticalOptions = LayoutOptions.Center };
        _email = Theme.Input("البريد الإلكتروني", Session.SavedEmail);
        _password = Theme.Input("كلمة المرور", Session.SavedPassword); _password.IsPassword = true;
        _serverUrl = Theme.Input("عنوان الخادم", Session.BaseUrl);
        _rememberMe = new CheckBox { IsChecked = Session.RememberMe, Color = Theme.Primary };

        var loginBtn = Theme.Filled("دخول"); loginBtn.Clicked += (s, e) => OnLoginClicked();

        var testConnBtn = Theme.Ghost("⚡ اختبار الاتصال");
        var testResultLbl = new Label
        {
            HorizontalTextAlignment = TextAlignment.Center,
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            TextColor = Theme.Ink,
            Margin = new Thickness(0, 4)
        };

        testConnBtn.Clicked += (s, e) => {
            var url = _serverUrl.Text?.Trim();
            if (string.IsNullOrWhiteSpace(url)) {
                testResultLbl.Text = "يرجى إدخال عنوان الخادم أولاً";
                testResultLbl.TextColor = Theme.Danger;
                return;
            }
            Session.BaseUrl = url;
            testResultLbl.Text = "جاري اختبار الاتصال...";
            testResultLbl.TextColor = Theme.Muted;

            Task.Run(async () => {
                try {
                    var res = await ApiClient.TestConnectionAsync(url);
                    MainThread.BeginInvokeOnMainThread(() => {
                        testResultLbl.Text = res.message;
                        testResultLbl.TextColor = (res.serverOk && res.apiOk && res.authOk) ? Theme.Success : Theme.Warning;
                    });
                } catch (Exception ex) {
                    MainThread.BeginInvokeOnMainThread(() => {
                        testResultLbl.Text = "خطأ في الاتصال: " + ex.Message;
                        testResultLbl.TextColor = Theme.Danger;
                    });
                }
            });
        };

        root.Add(Theme.H1("تسجيل الدخول"));
        root.Add(Theme.CardView(
            _email,
            _password,
            _serverUrl,
            new HorizontalStackLayout { _rememberMe, new Label { Text = "تذكرني", VerticalTextAlignment = TextAlignment.Center } },
            loginBtn,
            testConnBtn,
            testResultLbl,
            _status,
            _spin
        ));
        Show(new ScrollView { Content = root });
    }

    public void ShowHome()
    {
        var root = new VerticalStackLayout { Spacing = 20, Padding = 20, BackgroundColor = Theme.Bg };
        var head = new HorizontalStackLayout { Spacing = 12, HorizontalOptions = LayoutOptions.Fill };

        var userBlock = new HorizontalStackLayout {
            Spacing = 12,
            HorizontalOptions = LayoutOptions.StartAndExpand,
            Children = {
                new Border { WidthRequest=40, HeightRequest=40, BackgroundColor=Theme.Primary, StrokeShape=new RoundRectangle{CornerRadius=20}, Content=new Label{Text="U", TextColor=Colors.White, HorizontalTextAlignment=TextAlignment.Center, VerticalTextAlignment=TextAlignment.Center} },
                new VerticalStackLayout { Theme.P("مرحباً بك،"), new Label { Text = Session.UserEmail, FontAttributes = FontAttributes.Bold, TextColor = Theme.Ink } }
            }
        };

        var settingsHeaderBtn = new Button { Text = "⚙️ الإعدادات", FontSize = 12, TextColor = Theme.Primary, BackgroundColor = Colors.Transparent, BorderColor = Theme.Primary, BorderWidth = 1, CornerRadius = 12, HeightRequest = 36 };
        settingsHeaderBtn.Clicked += (s, e) => ShowSettings();

        head.Add(userBlock);
        head.Add(settingsHeaderBtn);
        root.Add(head);

        var pending = LocalStore.CountPending();
        var syncBadge = Theme.Chip(pending + " عمليات معلقة (انقر للمزامنة)", pending > 0 ? Theme.WarningSoft : Theme.SuccessSoft, pending > 0 ? Theme.Warning : Theme.Success);
        var syncCard = Theme.CardView(new Label { Text = "حالة المزامنة", FontSize = 12, TextColor = Theme.Muted }, syncBadge);
        var syncTgr = new TapGestureRecognizer();
        syncTgr.Tapped += (s, e) => {
            Task.Run(() => {
                SyncEngine.RunOnce();
                MainThread.BeginInvokeOnMainThread(ShowHome);
            });
        };
        syncCard.GestureRecognizers.Add(syncTgr);
        root.Add(syncCard);

        var grid = new Grid {
            ColumnSpacing = 12,
            RowSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = GridLength.Star } }
        };

        void AddBtn(string icon, string title, Color clr, Action act, int r, int c) {
            var content = new VerticalStackLayout { Spacing = 4 };
            content.Add(new Label { Text = icon, FontSize = 24, HorizontalTextAlignment = TextAlignment.Center });
            content.Add(new Label { Text = title, FontSize = 14, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center });
            var card = Theme.CardView(content);
            var tgr = new TapGestureRecognizer(); tgr.Tapped += (s, e) => act(); card.GestureRecognizers.Add(tgr);
            grid.Add(card, c, r);
        }

        AddBtn("🧾", "مبيعات", Theme.Primary, ShowInvoice, 0, 0);
        AddBtn("🛒", "مشتريات", Theme.Success, ShowPurchases, 0, 1);
        AddBtn("💵", "تحصيل ميداني", Theme.Secondary, ShowCustodyCollection, 1, 0);
        AddBtn("🔄", "مردودات", Theme.Danger, ShowSalesReturns, 1, 1);
        AddBtn("🔎", "استعلام الفواتير", Theme.Primary, ShowInvoiceSearch, 2, 0);
        AddBtn("📦", "الأصناف", Theme.Muted, ShowItems, 2, 1);
        AddBtn("📄", "كشوفات", Theme.Warning, ShowStatement, 3, 0);
        AddBtn("🔍", "الرقابة الميدانية", Theme.Secondary, ShowMerchandising, 3, 1);
        AddBtn("⚙️", "الإعدادات", Theme.Muted, ShowSettings, 4, 0);

        root.Add(grid);
        var logoutBtn = Theme.Ghost("خروج"); logoutBtn.Clicked += (s, e) => ShowLogin(); root.Add(logoutBtn);
        Show(new ScrollView { Content = root });
        Task.Run(RefreshDataSilent);
    }

    public void ShowSettings()
    {
        var root = new VerticalStackLayout { Spacing = 15, Padding = 20 };
        var head = new HorizontalStackLayout { Spacing = 10, Children = { Theme.Ghost("←"), Theme.H1("إعدادات التطبيق") } };
        var back = (Button)head.Children[0]; back.WidthRequest = 50; back.Clicked += (s, e) => ShowHome();

        // 1. اللغة
        var langLbl = Theme.Section("لغة التطبيق / App Language");
        var langPicker = Theme.Dropdown("اختر اللغة");
        langPicker.Items.Add("العربية (Arabic)");
        langPicker.Items.Add("English (إنجليزي)");
        langPicker.SelectedIndex = Session.Language == "en" ? 1 : 0;

        // 2. العملة
        var currLbl = Theme.Section("عملة النظام / Currency");
        var currPicker = Theme.Dropdown("اختر العملة");
        var currencies = new List<(string name, string symbol)>
        {
            ("ريال سعودي (ر.س)", "ر.س"),
            ("دينار أردني (د.أ)", "د.أ"),
            ("جنيه مصري (ج.م)", "ج.م"),
            ("درهم إماراتي (د.إ)", "د.إ"),
            ("ريال عماني (ر.ع)", "ر.ع"),
            ("ريال قطري (ر.ق)", "ر.ق"),
            ("دينار كويتي (د.ك)", "د.ك"),
            ("دولار أمريكي ($)", "$"),
            ("عملة مخصصة...", "")
        };

        foreach (var c in currencies) currPicker.Items.Add(c.name);

        var selectedCurrIndex = currencies.FindIndex(x => x.symbol == Session.CurrencySymbol);
        if (selectedCurrIndex >= 0) currPicker.SelectedIndex = selectedCurrIndex;
        else currPicker.SelectedIndex = currencies.Count - 1;

        var customCurrInput = Theme.Input("رمز العملة المخصصة", Session.CurrencySymbol);
        customCurrInput.IsVisible = currPicker.SelectedIndex == currencies.Count - 1;

        currPicker.SelectedIndexChanged += (s, e) => {
            if (currPicker.SelectedIndex == currencies.Count - 1) {
                customCurrInput.IsVisible = true;
            } else if (currPicker.SelectedIndex >= 0 && currPicker.SelectedIndex < currencies.Count - 1) {
                customCurrInput.IsVisible = false;
                customCurrInput.Text = currencies[currPicker.SelectedIndex].symbol;
            }
        };

        // 3. عنوان الخادم
        var serverLbl = Theme.Section("عنوان الخادم / Server Base URL");
        var serverInput = Theme.Input("عنوان الخادم", Session.BaseUrl);

        var statusLbl = new Label { HorizontalTextAlignment = TextAlignment.Center, FontAttributes = FontAttributes.Bold, Margin = 10 };

        var saveBtn = Theme.Filled("حفظ الإعدادات");
        saveBtn.Clicked += (s, e) => {
            Session.Language = langPicker.SelectedIndex == 1 ? "en" : "ar";
            FlowDirection = Session.Language == "en" ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;

            if (currPicker.SelectedIndex == currencies.Count - 1) {
                var customSym = customCurrInput.Text?.Trim();
                Session.CurrencySymbol = string.IsNullOrEmpty(customSym) ? "ر.س" : customSym;
            } else if (currPicker.SelectedIndex >= 0) {
                Session.CurrencySymbol = currencies[currPicker.SelectedIndex].symbol;
            }

            if (!string.IsNullOrWhiteSpace(serverInput.Text)) {
                Session.BaseUrl = serverInput.Text.Trim();
            }

            statusLbl.Text = "تم حفظ الإعدادات بنجاح ✓";
            statusLbl.TextColor = Theme.Success;
        };

        root.Add(head);
        root.Add(Theme.CardView(
            langLbl, langPicker,
            currLbl, currPicker, customCurrInput,
            serverLbl, serverInput,
            saveBtn, statusLbl
        ));

        Show(new ScrollView { Content = root });
    }

    private void OnLoginClicked()
    {
        _status.Text = "جاري الدخول..."; _spin.IsRunning = true; _spin.IsVisible = true;
        Session.BaseUrl = _serverUrl.Text;
        Task.Run(() => {
            try {
                var resp = ApiClient.LoginSync(_email.Text, _password.Text);
                if (!string.IsNullOrEmpty(resp.Token)) {
                    Session.Token = resp.Token; Session.UserEmail = resp.UserEmail;
                    if (_rememberMe.IsChecked) { Session.SavedEmail = _email.Text; Session.SavedPassword = _password.Text; Session.RememberMe = true; }
                    MainThread.BeginInvokeOnMainThread(ShowHome);
                } else { MainThread.BeginInvokeOnMainThread(() => { _status.Text = "خطأ في البيانات"; _spin.IsRunning = false; }); }
            } catch (Exception ex) { MainThread.BeginInvokeOnMainThread(() => { _status.Text = "فشل الاتصال: " + ex.Message; _spin.IsRunning = false; }); }
        });
    }

    private async void RefreshDataSilent()
    {
        try {
            var its = await ApiClient.ItemsAsync("");
            var cus = await ApiClient.CustomersAsync("");
            var sups = await ApiClient.SuppliersAsync("");
            var whs = await ApiClient.WarehousesAsync();
            LocalStore.CacheAll(its, cus, sups, whs);
            MainThread.BeginInvokeOnMainThread(() => { _items = its; _custs = cus; _sups = sups; _whs = whs; });
        } catch { }
    }

    // 📸 ماسح الباركود العام (مع طلب الصلاحيات وتحسين الأمان لمنع إغلاق التطبيق)
    private async void StartScanner(Action<string> onCodeCaptured)
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Camera>();
            }

            if (status != PermissionStatus.Granted)
            {
                if (Application.Current?.MainPage != null)
                {
                    await Application.Current.MainPage.DisplayAlert("تنبيه الصلاحية", "يلزم السماح بصلاحية استخدام الكاميرا للتمكن من مسح الباركود.", "موافق");
                }
                return;
            }

            var scanPage = new ContentPage { Title = "مسح الباركود" };
            var cameraView = new CameraBarcodeReaderView
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                Options = new BarcodeReaderOptions
                {
                    Formats = BarcodeFormat.Code128 | BarcodeFormat.Ean13 | BarcodeFormat.QrCode,
                    AutoRotate = true,
                    Multiple = false
                },
                IsDetecting = true
            };

            bool isHandled = false;
            cameraView.BarcodesDetected += (s, e) => {
                var first = e.Results?.FirstOrDefault();
                if (first != null && !string.IsNullOrWhiteSpace(first.Value) && !isHandled) {
                    isHandled = true;
                    MainThread.BeginInvokeOnMainThread(async () => {
                        try
                        {
                            cameraView.IsDetecting = false;
                            await Navigation.PopModalAsync();
                            onCodeCaptured?.Invoke(first.Value);
                        }
                        catch { }
                    });
                }
            };

            var closeBtn = Theme.Ghost("إغلاق الكاميرا");
            closeBtn.Clicked += async (s, e) => {
                if (!isHandled)
                {
                    isHandled = true;
                    try
                    {
                        cameraView.IsDetecting = false;
                        await Navigation.PopModalAsync();
                    }
                    catch { }
                }
            };

            scanPage.Disappearing += (s, e) => {
                try
                {
                    cameraView.IsDetecting = false;
                }
                catch { }
            };

            scanPage.Content = new Grid {
                Children = { cameraView, new VerticalStackLayout { VerticalOptions = LayoutOptions.End, Padding = 20, Children = { closeBtn } } }
            };

            await Navigation.PushModalAsync(scanPage);
        }
        catch (Exception ex)
        {
            if (Application.Current?.MainPage != null)
            {
                await Application.Current.MainPage.DisplayAlert("خطأ في الكاميرا", $"تعذر فتح الكاميرا: {ex.Message}", "موافق");
            }
        }
    }

    public static decimal ParseDecimal(string? input, decimal fallback = 0m)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;
        var sb = new StringBuilder();
        foreach (var ch in input)
        {
            if (ch >= '٠' && ch <= '٩')
                sb.Append((char)('0' + (ch - '٠')));
            else if (ch >= '0' && ch <= '9')
                sb.Append(ch);
            else if (ch == '.' || ch == ',')
                sb.Append('.');
        }
        var normalized = sb.ToString();
        if (decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            return result;
        return fallback;
    }

    public static decimal ParseTaxRate(string? taxRateStr)
    {
        if (string.IsNullOrWhiteSpace(taxRateStr)) return 0m;
        var cleaned = taxRateStr.Replace("%", "").Trim();
        var rate = ParseDecimal(cleaned, 0m);
        if (rate > 0 && rate < 1) return rate * 100m;
        return rate;
    }

    public void ShowItems()
    {
        var root = new VerticalStackLayout { Spacing = 15, Padding = 20 };
        var head = new HorizontalStackLayout { Spacing = 10, Children = { Theme.Ghost("←"), Theme.H1("الأصناف") } };
        var back = (Button)head.Children[0]; back.WidthRequest = 50; back.Clicked += (s, e) => ShowHome();

        _search = Theme.LargeInput("🔍 بحث عن صنف بالاسم أو الكود...");

        var btn = Theme.Filled("بحث");
        void btn_SearchClicked() {
            var term = _search.Text?.ToLower() ?? "";
            var hits = _items.Where(x => x.NameAr.ToLower().Contains(term) || (x.Barcode?.Contains(term) ?? false) || x.Code.ToLower().Contains(term)).Take(50).ToList();
            _itemsScroll.Clear();
            foreach (var h in hits) {
                var tax = ParseTaxRate(h.TaxRate);
                _itemsScroll.Add(Theme.CardView(
                    new Label { Text = h.NameAr, FontAttributes = FontAttributes.Bold, FontSize = 16 },
                    new Label { Text = $"الكود: {h.Code} | السعر: {h.SalePrice} {Session.CurrencySymbol} | الضريبة: {tax}% | الرصيد: {h.CurrentStock}", FontSize = 12, TextColor = Theme.Muted }
                ));
            }
        }
        btn.Clicked += (s, e) => btn_SearchClicked();

        var scanBtn = Theme.Ghost("📷 مسح باركود");
        scanBtn.Clicked += (s, e) => StartScanner(code => { _search.Text = code; btn_SearchClicked(); });

        root.Add(head); root.Add(Theme.CardView(_search, scanBtn, btn));
        _itemsScroll = new VerticalStackLayout { Spacing = 10 }; root.Add(_itemsScroll);
        Show(new ScrollView { Content = root });
    }

    public void ShowInvoice()
    {
        var root = new VerticalStackLayout { Spacing = 15, Padding = 20 };
        var head = new HorizontalStackLayout { Spacing = 10, Children = { Theme.Ghost("←"), Theme.H1("مبيعات") } };
        var back = (Button)head.Children[0]; back.WidthRequest = 50; back.Clicked += (s, e) => ShowHome();
        
        _cuPicker = Theme.Dropdown("العميل"); foreach (var c in _custs) _cuPicker.Items.Add(c.NameAr);
        if (_custs.Count > 0 && _cuPicker.SelectedIndex < 0) _cuPicker.SelectedIndex = 0;

        _whPicker = Theme.Dropdown("المستودع"); foreach (var w in _whs) _whPicker.Items.Add(w.NameAr);
        if (_whs.Count > 0 && _whPicker.SelectedIndex < 0) _whPicker.SelectedIndex = 0;

        _invoiceItemSearch = Theme.LargeInput("🔍 بحث عن صنف المبيعات...");
        var scanBtn = Theme.Ghost("📷 مسح صنف");
        scanBtn.Clicked += (s, e) => StartScanner(code => { _invoiceItemSearch.Text = code; });

        _itPicker = Theme.Dropdown("اختر الصنف...");
        var itemTaxInfoLbl = new Label { FontSize = 12, TextColor = Theme.Primary, FontAttributes = FontAttributes.Bold };

        _qty = Theme.Input("الكمية", "1");
        _price = Theme.Input("السعر");

        void PopulateItemHits(string term) {
            _itemHits = string.IsNullOrWhiteSpace(term)
                ? _items.Take(20).ToList()
                : _items.Where(x => x.NameAr.ToLower().Contains(term.ToLower()) || (x.Barcode?.Contains(term) ?? false) || x.Code.ToLower().Contains(term.ToLower())).Take(20).ToList();

            _itPicker.Items.Clear();
            foreach (var h in _itemHits) _itPicker.Items.Add($"{h.NameAr} ({h.SalePrice} {Session.CurrencySymbol})");

            if (_itemHits.Count > 0) {
                _itPicker.SelectedIndex = 0;
                if (_price != null) _price.Text = _itemHits[0].SalePrice;
                itemTaxInfoLbl.Text = $"السعر: {_itemHits[0].SalePrice} {Session.CurrencySymbol} | نسبة الضريبة: {ParseTaxRate(_itemHits[0].TaxRate)}%";
            }
        }

        // إدراج الأصناف الافتراضية
        PopulateItemHits("");

        _invoiceItemSearch.TextChanged += (s, e) => PopulateItemHits(_invoiceItemSearch.Text ?? "");

        _itPicker.SelectedIndexChanged += (s, e) => {
            if (_itPicker.SelectedIndex >= 0 && _itPicker.SelectedIndex < _itemHits.Count) {
                var it = _itemHits[_itPicker.SelectedIndex];
                if (_price != null) _price.Text = it.SalePrice;
                itemTaxInfoLbl.Text = $"السعر: {it.SalePrice} {Session.CurrencySymbol} | نسبة الضريبة: {ParseTaxRate(it.TaxRate)}%";
            }
        };

        var cartLinesLayout = new VerticalStackLayout { Spacing = 8 };
        var netLbl = new Label { FontSize = 14, TextColor = Theme.Ink, FontAttributes = FontAttributes.Bold };
        var taxLbl = new Label { FontSize = 14, TextColor = Theme.Primary, FontAttributes = FontAttributes.Bold };
        var grandTotalLbl = new Label { FontSize = 18, TextColor = Theme.Primary, FontAttributes = FontAttributes.Bold };

        List<(ItemDto Item, decimal Qty, decimal Price)> salesCart = new();

        void UpdateCartUI() {
            cartLinesLayout.Clear();
            _lines.Clear();
            decimal netSum = 0m;
            decimal taxSum = 0m;

            for (int i = 0; i < salesCart.Count; i++) {
                var entry = salesCart[i];
                var index = i;
                _lines.Add(new InvoiceLineDto(entry.Item.Id, entry.Qty.ToString("0.##", CultureInfo.InvariantCulture), entry.Price.ToString("0.##", CultureInfo.InvariantCulture)));

                var lineNet = entry.Qty * entry.Price;
                var taxRate = ParseTaxRate(entry.Item.TaxRate);
                var lineTax = lineNet * (taxRate / 100m);
                var lineTotal = lineNet + lineTax;

                netSum += lineNet;
                taxSum += lineTax;

                var removeBtn = new Button { Text = "❌ حذف", FontSize = 11, TextColor = Theme.Danger, BackgroundColor = Colors.Transparent, Padding = 0, HeightRequest = 30 };
                removeBtn.Clicked += (s, e) => {
                    salesCart.RemoveAt(index);
                    UpdateCartUI();
                };

                cartLinesLayout.Add(Theme.CardView(
                    new HorizontalStackLayout {
                        HorizontalOptions = LayoutOptions.Fill,
                        Children = {
                            new Label { Text = entry.Item.NameAr, FontAttributes = FontAttributes.Bold, FontSize = 15, HorizontalOptions = LayoutOptions.StartAndExpand },
                            removeBtn
                        }
                    },
                    new Label { Text = $"الكمية: {entry.Qty} × {entry.Price} = {lineNet:N2} | ضريبة ({taxRate}%): {lineTax:N2} | الإجمالي: {lineTotal:N2} {Session.CurrencySymbol}", FontSize = 12, TextColor = Theme.Muted }
                ));
            }

            var curr = Session.CurrencySymbol;
            netLbl.Text = $"الصافي قبل الضريبة: {netSum:N2} {curr}";
            taxLbl.Text = $"إجمالي ضريبة المبيعات: {taxSum:N2} {curr}";
            grandTotalLbl.Text = $"الإجمالي الصافي الشامل للضريبة: {(netSum + taxSum):N2} {curr}";
            _linesLbl.Text = salesCart.Count == 0 ? "السلة فارغة" : $"{salesCart.Count} أصناف في السلة (المجموع: {(netSum + taxSum):N2} {curr})";
        }

        var add = Theme.Filled("إضافة لسلة المبيعات");
        add.Clicked += (s, e) => {
            if (_itPicker.SelectedIndex < 0 || _itPicker.SelectedIndex >= _itemHits.Count) return;
            var it = _itemHits[_itPicker.SelectedIndex];

            var q = ParseDecimal(_qty.Text, 1m);
            if (q <= 0) q = 1m;

            var defaultPrice = ParseDecimal(it.SalePrice, 0m);
            var p = ParseDecimal(_price.Text, defaultPrice);

            salesCart.Add((it, q, p));
            UpdateCartUI();
        };

        _linesLbl = new Label { Text = "السلة فارغة", HorizontalTextAlignment = TextAlignment.Center, FontAttributes = FontAttributes.Bold };
        var save = Theme.Filled("حفظ وإرسال الفاتورة");
        save.Clicked += (s, e) => OnCreateInvoiceClicked();
        
        root.Add(head);
        root.Add(Theme.CardView(_cuPicker, _whPicker));
        root.Add(Theme.CardView(_invoiceItemSearch, scanBtn, _itPicker, itemTaxInfoLbl, _qty, _price, add));
        root.Add(Theme.CardView(_linesLbl, cartLinesLayout, netLbl, taxLbl, grandTotalLbl, save, _invoiceResult = new Label { HorizontalTextAlignment = TextAlignment.Center, FontAttributes = FontAttributes.Bold, Margin = 10 }));
        Show(new ScrollView { Content = root });
    }

    private void OnCreateInvoiceClicked()
    {
        if (_cuPicker.SelectedIndex < 0) {
            _invoiceResult.Text = "يرجى اختيار العميل أولاً";
            _invoiceResult.TextColor = Theme.Danger;
            return;
        }
        if (_whPicker.SelectedIndex < 0) {
            _invoiceResult.Text = "يرجى اختيار المستودع أولاً";
            _invoiceResult.TextColor = Theme.Danger;
            return;
        }
        if (_lines.Count == 0) {
            _invoiceResult.Text = "سلة المبيعات فارغة، يرجى إضافة صنف واحد على الأقل";
            _invoiceResult.TextColor = Theme.Danger;
            return;
        }

        _invoiceResult.Text = "جاري الحفظ والإرسال...";
        _invoiceResult.TextColor = Theme.Primary;

        var custId = _custs[_cuPicker.SelectedIndex].Id;
        var whId = _whs[_whPicker.SelectedIndex].Id;
        var linesCopy = new List<InvoiceLineDto>(_lines);

        Task.Run(() => {
            try {
                var res = ApiClient.CreateInvoiceSync(linesCopy, custId, whId, 1, "مبيعات جوال");
                MainThread.BeginInvokeOnMainThread(() => {
                    _invoiceResult.Text = "تم الحفظ والإرسال بنجاح ✓ | فاتورة: " + res.InvoiceNumber;
                    _invoiceResult.TextColor = Theme.Success;
                    _lines.Clear();
                    _linesLbl.Text = "السلة فارغة";
                });
            } catch (Exception ex) {
                // حفظ الفاتورة في طابور المزامنة المحلي إذا انقطع الاتصال بالخادم
                try {
                    var clientKey = Guid.NewGuid().ToString();
                    var payload = ApiClient.MakeInvoicePayload(linesCopy, custId, whId, "مبيعات جوال", clientKey, 1, "");
                    LocalStore.Enqueue(clientKey, payload);

                    MainThread.BeginInvokeOnMainThread(() => {
                        _invoiceResult.Text = "تم حفظ الفاتورة محلياً في قائمة الانتظار ✓ (سيتم رفعها أوتوماتيكياً للمستودع عند الاتصال)";
                        _invoiceResult.TextColor = Theme.Warning;
                        _lines.Clear();
                        _linesLbl.Text = "السلة فارغة";
                    });
                } catch (Exception ex2) {
                    MainThread.BeginInvokeOnMainThread(() => {
                        _invoiceResult.Text = "خطأ في الحفظ: " + ex2.Message;
                        _invoiceResult.TextColor = Theme.Danger;
                    });
                }
            }
        });
    }

    public void ShowInvoiceSearch()
    {
        var root = new VerticalStackLayout { Spacing = 15, Padding = 20 };
        var head = new HorizontalStackLayout { Spacing = 10, Children = { Theme.Ghost("←"), Theme.H1("استعلام الفواتير") } };
        var back = (Button)head.Children[0]; back.WidthRequest = 50; back.Clicked += (s, e) => ShowHome();

        var invSearch = Theme.LargeInput("🔍 ابحث برقم الفاتورة أو اسم العميل...");
        var statusLbl = new Label { HorizontalTextAlignment = TextAlignment.Center, FontAttributes = FontAttributes.Bold };
        var resultScroll = new VerticalStackLayout { Spacing = 10 };

        void DoSearch() {
            var term = invSearch.Text?.Trim() ?? "";
            statusLbl.Text = "جاري البحث...";
            Task.Run(() => {
                try {
                    var list = ApiClient.SearchInvoicesSync(term);
                    MainThread.BeginInvokeOnMainThread(() => {
                        resultScroll.Clear();
                        if (list.Count == 0) {
                            statusLbl.Text = "لم يتم العثور على أي فواتير مطابقة";
                        } else {
                            statusLbl.Text = $"تم العثور على {list.Count} فاتورة";
                            foreach (var item in list) {
                                resultScroll.Add(Theme.CardView(
                                    new Label { Text = "فاتورة: " + item.Number, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary },
                                    new Label { Text = "العميل: " + item.Customer, FontAttributes = FontAttributes.Bold },
                                    new Label { Text = $"التاريخ: {item.Date} | الإجمالي: {item.Total} {Session.CurrencySymbol}", FontSize = 13, TextColor = Theme.Muted }
                                ));
                            }
                        }
                    });
                } catch (Exception ex) {
                    MainThread.BeginInvokeOnMainThread(() => statusLbl.Text = "خطأ في البحث: " + ex.Message);
                }
            });
        }

        var searchBtn = Theme.Filled("بحث عن الفواتير");
        searchBtn.Clicked += (s, e) => DoSearch();
        invSearch.Completed += (s, e) => DoSearch();

        var scanBtn = Theme.Ghost("📷 مسح رقم الفاتورة");
        scanBtn.Clicked += (s, e) => StartScanner(code => { invSearch.Text = code; DoSearch(); });

        root.Add(head);
        root.Add(Theme.CardView(invSearch, scanBtn, searchBtn, statusLbl));
        root.Add(resultScroll);
        Show(new ScrollView { Content = root });
    }

    public void ShowPurchases()
    {
        var root = new VerticalStackLayout { Spacing = 15, Padding = 20 };
        var head = new HorizontalStackLayout { Spacing = 10, Children = { Theme.Ghost("←"), Theme.H1("مشتريات") } };
        var back = (Button)head.Children[0]; back.WidthRequest = 50; back.Clicked += (s, e) => ShowHome();

        var suPicker = Theme.Dropdown("المورد"); foreach (var sup in _sups) suPicker.Items.Add(sup.NameAr);
        var whPicker = Theme.Dropdown("المستودع"); foreach (var w in _whs) whPicker.Items.Add(w.NameAr);

        var purchaseItemSearch = Theme.LargeInput("🔍 بحث عن صنف للمشتريات...");
        var itPicker = Theme.Dropdown("النتائج...");
        List<ItemDto> itemHits = new();

        var itemTaxInfoLbl = new Label { FontSize = 12, TextColor = Theme.Primary, FontAttributes = FontAttributes.Bold };
        var costEntry = Theme.Input("تكلفة الوحدة");
        var qtyEntry = Theme.Input("الكمية", "1");

        void PopulatePurchaseItemHits(string term) {
            itemHits = string.IsNullOrWhiteSpace(term)
                ? _items.Take(20).ToList()
                : _items.Where(x => x.NameAr.ToLower().Contains(term.ToLower()) || (x.Barcode?.Contains(term) ?? false) || x.Code.ToLower().Contains(term.ToLower())).Take(20).ToList();

            itPicker.Items.Clear();
            foreach (var h in itemHits) itPicker.Items.Add($"{h.NameAr} ({h.SalePrice} {Session.CurrencySymbol})");

            if (itemHits.Count > 0) {
                itPicker.SelectedIndex = 0;
                costEntry.Text = itemHits[0].SalePrice;
                itemTaxInfoLbl.Text = $"التكلفة: {itemHits[0].SalePrice} {Session.CurrencySymbol} | نسبة الضريبة: {ParseTaxRate(itemHits[0].TaxRate)}%";
            }
        }

        PopulatePurchaseItemHits("");

        purchaseItemSearch.TextChanged += (s, e) => PopulatePurchaseItemHits(purchaseItemSearch.Text ?? "");

        itPicker.SelectedIndexChanged += (s, e) => {
            if (itPicker.SelectedIndex >= 0 && itPicker.SelectedIndex < itemHits.Count) {
                var it = itemHits[itPicker.SelectedIndex];
                costEntry.Text = it.SalePrice;
                itemTaxInfoLbl.Text = $"التكلفة: {it.SalePrice} {Session.CurrencySymbol} | نسبة الضريبة: {ParseTaxRate(it.TaxRate)}%";
            }
        };

        var scanBtn = Theme.Ghost("📷 مسح صنف شراء");
        scanBtn.Clicked += (s, e) => StartScanner(code => { purchaseItemSearch.Text = code; });

        List<InvoiceLineDto> purchaseLines = new();
        List<(ItemDto Item, decimal Qty, decimal Cost)> purchaseCart = new();

        var cartLinesLayout = new VerticalStackLayout { Spacing = 8 };
        var netLbl = new Label { FontSize = 13, TextColor = Theme.Ink };
        var taxLbl = new Label { FontSize = 13, TextColor = Theme.Primary, FontAttributes = FontAttributes.Bold };
        var grandTotalLbl = new Label { FontSize = 16, TextColor = Theme.Primary, FontAttributes = FontAttributes.Bold };
        var linesLbl = new Label { Text = "السلة فارغة", HorizontalTextAlignment = TextAlignment.Center, FontAttributes = FontAttributes.Bold };

        void UpdatePurchaseCartUI() {
            cartLinesLayout.Clear();
            purchaseLines.Clear();
            decimal netSum = 0m;
            decimal taxSum = 0m;

            for (int i = 0; i < purchaseCart.Count; i++) {
                var entry = purchaseCart[i];
                var index = i;
                purchaseLines.Add(new InvoiceLineDto(entry.Item.Id, entry.Qty.ToString("0.##", CultureInfo.InvariantCulture), entry.Cost.ToString("0.##", CultureInfo.InvariantCulture)));

                var lineNet = entry.Qty * entry.Cost;
                var taxRate = ParseTaxRate(entry.Item.TaxRate);
                var lineTax = lineNet * (taxRate / 100m);
                var lineTotal = lineNet + lineTax;

                netSum += lineNet;
                taxSum += lineTax;

                var removeBtn = new Button { Text = "❌ حذف", FontSize = 11, TextColor = Theme.Danger, BackgroundColor = Colors.Transparent, Padding = 0, HeightRequest = 30 };
                removeBtn.Clicked += (s, e) => {
                    purchaseCart.RemoveAt(index);
                    UpdatePurchaseCartUI();
                };

                cartLinesLayout.Add(Theme.CardView(
                    new HorizontalStackLayout {
                        HorizontalOptions = LayoutOptions.Fill,
                        Children = {
                            new Label { Text = entry.Item.NameAr, FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.StartAndExpand },
                            removeBtn
                        }
                    },
                    new Label { Text = $"الكمية: {entry.Qty} × {entry.Cost} = {lineNet:N2} | ضريبة المشتريات ({taxRate}%): {lineTax:N2} | الإجمالي: {lineTotal:N2} {Session.CurrencySymbol}", FontSize = 12, TextColor = Theme.Muted }
                ));
            }

            var curr = Session.CurrencySymbol;
            netLbl.Text = $"مجموع المشتريات قبل الضريبة: {netSum:N2} {curr}";
            taxLbl.Text = $"إجمالي ضريبة المشتريات: {taxSum:N2} {curr}";
            grandTotalLbl.Text = $"إجمالي المشتريات الشامل للضريبة: {(netSum + taxSum):N2} {curr}";
            linesLbl.Text = purchaseCart.Count == 0 ? "السلة فارغة" : $"{purchaseCart.Count} أصناف في الفاتورة (المجموع: {(netSum + taxSum):N2} {curr})";
        }

        var add = Theme.Filled("إضافة صنف للمشتريات");
        add.Clicked += (s, e) => {
            if (itPicker.SelectedIndex < 0 || itPicker.SelectedIndex >= itemHits.Count) return;
            var it = itemHits[itPicker.SelectedIndex];

            var q = ParseDecimal(qtyEntry.Text, 1m);
            if (q <= 0) q = 1m;

            var defaultCost = ParseDecimal(it.SalePrice, 0m);
            var c = ParseDecimal(costEntry.Text, defaultCost);

            purchaseCart.Add((it, q, c));
            UpdatePurchaseCartUI();
        };

        var purchaseResult = new Label { HorizontalTextAlignment = TextAlignment.Center, FontAttributes = FontAttributes.Bold };
        var save = Theme.Filled("حفظ فاتورة الشراء");
        save.Clicked += (s, e) => {
            if (suPicker.SelectedIndex < 0 || whPicker.SelectedIndex < 0 || purchaseLines.Count == 0) {
                purchaseResult.Text = "يرجى اختيار المورد والمستودع وإضافة صنف واحد على الأقل";
                return;
            }
            purchaseResult.Text = "جاري الحفظ...";
            var supId = _sups[suPicker.SelectedIndex].Id;
            var whId = _whs[whPicker.SelectedIndex].Id;
            Task.Run(() => {
                try {
                    var res = ApiClient.CreatePurchaseInvoiceSync(supId, whId, "مشتريات جوال", purchaseLines);
                    MainThread.BeginInvokeOnMainThread(() => {
                        purchaseResult.Text = "تم إنشاء فاتورة الشراء بنجاح";
                        purchaseCart.Clear();
                        UpdatePurchaseCartUI();
                    });
                } catch (Exception ex) {
                    MainThread.BeginInvokeOnMainThread(() => purchaseResult.Text = "خطأ: " + ex.Message);
                }
            });
        };

        root.Add(head);
        root.Add(Theme.CardView(suPicker, whPicker));
        root.Add(Theme.CardView(purchaseItemSearch, scanBtn, itPicker, itemTaxInfoLbl, qtyEntry, costEntry, add));
        root.Add(Theme.CardView(linesLbl, cartLinesLayout, netLbl, taxLbl, grandTotalLbl, save, purchaseResult));
        Show(new ScrollView { Content = root });
    }

    public void ShowCustodyCollection()
    {
        var root = new VerticalStackLayout { Spacing = 15, Padding = 20 };
        var head = new HorizontalStackLayout { Spacing = 10, Children = { Theme.Ghost("←"), Theme.H1("تحصيل ميداني") } };
        var back = (Button)head.Children[0]; back.WidthRequest = 50; back.Clicked += (s, e) => ShowHome();

        var cuPicker = Theme.Dropdown("اختر العميل"); foreach (var c in _custs) cuPicker.Items.Add(c.NameAr);
        var amountEntry = Theme.Input("المبلغ المحصل");
        var noteEntry = Theme.Input("ملاحظات / رقم الشيك");
        var resultLbl = new Label { HorizontalTextAlignment = TextAlignment.Center, FontAttributes = FontAttributes.Bold };

        var saveBtn = Theme.Filled("حفظ سند القبض");
        saveBtn.Clicked += (s, e) => {
            if (cuPicker.SelectedIndex < 0 || string.IsNullOrWhiteSpace(amountEntry.Text)) {
                resultLbl.Text = "يرجى اختيار العميل وإدخال المبلغ";
                return;
            }
            resultLbl.Text = "جاري حفظ التحصيل...";
            var cuId = _custs[cuPicker.SelectedIndex].Id;
            var amount = ParseDecimal(amountEntry.Text, 0m).ToString("0.##", CultureInfo.InvariantCulture);
            var note = noteEntry.Text?.Trim() ?? "";

            Task.Run(() => {
                try {
                    var entryNum = ApiClient.CollectCustodySync(cuId, amount, note);
                    MainThread.BeginInvokeOnMainThread(() => {
                        resultLbl.Text = "تم حفظ سند القبض بنجاح: " + entryNum;
                        amountEntry.Text = "";
                        noteEntry.Text = "";
                    });
                } catch (Exception ex) {
                    MainThread.BeginInvokeOnMainThread(() => resultLbl.Text = "خطأ: " + ex.Message);
                }
            });
        };

        root.Add(head);
        root.Add(Theme.CardView(cuPicker, amountEntry, noteEntry, saveBtn, resultLbl));
        Show(new ScrollView { Content = root });
    }

    public void ShowSalesReturns()
    {
        var root = new VerticalStackLayout { Spacing = 15, Padding = 20 };
        var head = new HorizontalStackLayout { Spacing = 10, Children = { Theme.Ghost("←"), Theme.H1("مردودات المبيعات") } };
        var back = (Button)head.Children[0]; back.WidthRequest = 50; back.Clicked += (s, e) => ShowHome();

        var invSearch = Theme.LargeInput("🔍 أدخل رقم الفاتورة أو اسم العميل للبحث...");
        var resultLbl = new Label { HorizontalTextAlignment = TextAlignment.Center, FontAttributes = FontAttributes.Bold };
        var infoLbl = new Label { FontAttributes = FontAttributes.Bold };
        var linesLayout = new VerticalStackLayout { Spacing = 10 };

        var netReturnLbl = new Label { FontSize = 13, TextColor = Theme.Ink };
        var taxReturnLbl = new Label { FontSize = 13, TextColor = Theme.Primary, FontAttributes = FontAttributes.Bold };
        var totalReturnLbl = new Label { FontSize = 16, TextColor = Theme.Primary, FontAttributes = FontAttributes.Bold };

        ReturnLookupDto? currentLookup = null;
        List<Entry> qtyEntries = new();
        List<decimal> itemTaxRates = new();

        void RecalculateReturnTaxes() {
            if (currentLookup == null) return;
            decimal netSum = 0m;
            decimal taxSum = 0m;

            for (int i = 0; i < currentLookup.Lines.Count; i++) {
                var ln = currentLookup.Lines[i];
                var qtyStr = qtyEntries[i].Text?.Trim() ?? "0";
                var q = ParseDecimal(qtyStr, 0m);
                var uPrice = ParseDecimal(ln.UnitPrice, 0m);

                if (q > 0 && uPrice > 0) {
                    var lineNet = q * uPrice;
                    var rate = itemTaxRates[i];
                    var lineTax = lineNet * (rate / 100m);
                    netSum += lineNet;
                    taxSum += lineTax;
                }
            }

            var curr = Session.CurrencySymbol;
            netReturnLbl.Text = $"المجموع قبل الضريبة: {netSum:N2} {curr}";
            taxReturnLbl.Text = $"ضريبة المردودات المسترجعة: {taxSum:N2} {curr}";
            totalReturnLbl.Text = $"صافي المردود الشامل للضريبة: {(netSum + taxSum):N2} {curr}";
        }

        void DoInvoiceLookup(string invNum) {
            if (string.IsNullOrEmpty(invNum)) return;
            resultLbl.Text = "جاري البحث...";
            Task.Run(() => {
                try {
                    var lookup = ApiClient.ReturnLookupSync(invNum);
                    currentLookup = lookup;
                    MainThread.BeginInvokeOnMainThread(() => {
                        infoLbl.Text = $"الفاتورة: {lookup.InvoiceNumber} | العميل: {lookup.CustomerName} | الإجمالي: {lookup.TotalAmount} {Session.CurrencySymbol}";
                        linesLayout.Clear();
                        qtyEntries.Clear();
                        itemTaxRates.Clear();

                        foreach (var ln in lookup.Lines) {
                            var matchedItem = _items.FirstOrDefault(x => x.Id == ln.ItemId || x.Code == ln.Code);
                            var taxRate = ParseTaxRate(matchedItem?.TaxRate);
                            itemTaxRates.Add(taxRate);

                            var qtyInput = Theme.Input("الكمية المردودة", "0");
                            qtyInput.TextChanged += (s, e) => RecalculateReturnTaxes();
                            qtyEntries.Add(qtyInput);

                            linesLayout.Add(Theme.CardView(
                                new Label { Text = ln.NameAr, FontAttributes = FontAttributes.Bold },
                                new Label { Text = $"السعر: {ln.UnitPrice} {Session.CurrencySymbol} | نسبة الضريبة: {taxRate}% | المتبقي القابل للرد: {ln.Remaining}", FontSize = 12, TextColor = Theme.Muted },
                                qtyInput
                            ));
                        }
                        RecalculateReturnTaxes();
                        resultLbl.Text = "تم العثور على الفاتورة";
                    });
                } catch (Exception ex) {
                    MainThread.BeginInvokeOnMainThread(() => resultLbl.Text = "خطأ في البحث: " + ex.Message);
                }
            });
        }

        var searchBtn = Theme.Filled("بحث عن الفاتورة");
        searchBtn.Clicked += (s, e) => DoInvoiceLookup(invSearch.Text?.Trim() ?? "");

        var scanBtn = Theme.Ghost("📷 مسح رقم الفاتورة");
        scanBtn.Clicked += (s, e) => StartScanner(code => { invSearch.Text = code; DoInvoiceLookup(code); });

        var saveReturnBtn = Theme.Filled("حفظ وإرسال المردود");
        saveReturnBtn.Clicked += (s, e) => {
            if (currentLookup == null) {
                resultLbl.Text = "يرجى البحث عن فاتورة أولاً";
                return;
            }
            var returnLines = new List<CreateReturnLineDto>();
            for (int i = 0; i < currentLookup.Lines.Count; i++) {
                var qtyStr = qtyEntries[i].Text?.Trim() ?? "0";
                var q = ParseDecimal(qtyStr, 0m);
                if (q > 0) {
                    returnLines.Add(new CreateReturnLineDto(currentLookup.Lines[i].ItemId, q.ToString("0.##", CultureInfo.InvariantCulture)));
                }
            }
            if (returnLines.Count == 0) {
                resultLbl.Text = "يرجى إدخال كمية مردودة لصنف واحد على الأقل";
                return;
            }

            resultLbl.Text = "جاري إنشاء المردود...";
            Task.Run(() => {
                try {
                    var ret = ApiClient.CreateSalesReturnSync(currentLookup.InvoiceId, currentLookup.WarehouseId, returnLines, "مردودات جوال");
                    MainThread.BeginInvokeOnMainThread(() => {
                        resultLbl.Text = "تم إنشاء المردود بنجاح: " + ret.ReturnNumber;
                        linesLayout.Clear();
                        currentLookup = null;
                        infoLbl.Text = "";
                        netReturnLbl.Text = "";
                        taxReturnLbl.Text = "";
                        totalReturnLbl.Text = "";
                    });
                } catch (Exception ex) {
                    MainThread.BeginInvokeOnMainThread(() => resultLbl.Text = "خطأ: " + ex.Message);
                }
            });
        };

        root.Add(head);
        root.Add(Theme.CardView(invSearch, scanBtn, searchBtn, infoLbl));
        root.Add(linesLayout);
        root.Add(Theme.CardView(netReturnLbl, taxReturnLbl, totalReturnLbl, saveReturnBtn, resultLbl));
        Show(new ScrollView { Content = root });
    }

    public void ShowStatement()
    {
        var root = new VerticalStackLayout { Spacing = 15, Padding = 20 };
        var head = new HorizontalStackLayout { Spacing = 10, Children = { Theme.Ghost("←"), Theme.H1("كشوفات الحسابات") } };
        var back = (Button)head.Children[0]; back.WidthRequest = 50; back.Clicked += (s, e) => ShowHome();

        var typePicker = Theme.Dropdown("نوع الحساب");
        typePicker.Items.Add("عميل");
        typePicker.Items.Add("مورد");
        typePicker.SelectedIndex = 0;

        var entityPicker = Theme.Dropdown("اختر الحساب");
        void PopulateEntities() {
            entityPicker.Items.Clear();
            if (typePicker.SelectedIndex == 0) {
                foreach (var c in _custs) entityPicker.Items.Add(c.NameAr);
            } else {
                foreach (var s in _sups) entityPicker.Items.Add(s.NameAr);
            }
        }
        PopulateEntities();
        typePicker.SelectedIndexChanged += (s, e) => PopulateEntities();

        var load = Theme.Filled("تحميل الكشف");
        load.Clicked += (s, e) => {
            if (entityPicker.SelectedIndex < 0) return;
            bool isCustomer = typePicker.SelectedIndex == 0;
            var id = isCustomer ? _custs[entityPicker.SelectedIndex].Id : _sups[entityPicker.SelectedIndex].Id;

            _stmtResult.Text = "جاري التحميل...";
            Task.Run(() => {
                try {
                    var res = isCustomer
                        ? ApiClient.GetCustomerStatementSync(id, DateTime.Today.AddMonths(-1), DateTime.Today)
                        : ApiClient.GetSupplierStatementSync(id, DateTime.Today.AddMonths(-1), DateTime.Today);
                    MainThread.BeginInvokeOnMainThread(() => {
                        _stmtResult.Text = $"{res.EntityName} | الرصيد النهائي: {res.ClosingBalance} {Session.CurrencySymbol}";
                        _stmtScroll.Clear();
                        foreach (var l in res.Lines) {
                            _stmtScroll.Add(Theme.CardView(
                                new Label { Text = l.Date + " | " + l.DocumentNumber + " | " + l.TypeNameAr, FontAttributes = FontAttributes.Bold },
                                new Label { Text = $"مدين: {l.Debit} | دائن: {l.Credit} | الرصيد: {l.Balance} {Session.CurrencySymbol}", FontSize = 12, TextColor = Theme.Muted }
                            ));
                        }
                    });
                } catch (Exception ex) { MainThread.BeginInvokeOnMainThread(() => _stmtResult.Text = "خطأ: " + ex.Message); }
            });
        };

        root.Add(head);
        root.Add(Theme.CardView(typePicker, entityPicker, load, _stmtResult = new Label()));
        _stmtScroll = new VerticalStackLayout { Spacing = 8 };
        root.Add(_stmtScroll);
        Show(new ScrollView { Content = root });
    }

    public void ShowMerchandising()
    {
        var root = new VerticalStackLayout { Spacing = 15, Padding = 20 };
        var head = new HorizontalStackLayout { Spacing = 10, Children = { Theme.Ghost("←"), Theme.H1("الرقابة الميدانية") } };
        var back = (Button)head.Children[0]; back.WidthRequest = 50; back.Clicked += (s, e) => ShowHome();

        var modePicker = Theme.Dropdown("نوع العملية");
        modePicker.Items.Add("فحص رف (جرد الرف)");
        modePicker.Items.Add("رصد سعر منافس");
        modePicker.SelectedIndex = 0;

        var itPicker = Theme.Dropdown("اختر الصنف");
        foreach (var item in _items) itPicker.Items.Add(item.NameAr);

        var scanBtn = Theme.Ghost("📷 مسح باركود الصنف");
        scanBtn.Clicked += (s, e) => StartScanner(code => {
            var it = _items.FirstOrDefault(x => x.Barcode == code || x.Code == code);
            if (it != null) {
                var idx = _items.IndexOf(it);
                itPicker.SelectedIndex = idx;
            }
        });

        var field1 = Theme.Input("الموقع (مثال: الرف A1 / فرع عمان)");
        var field2 = Theme.Input("الكمية الملاحظة");
        var notesField = Theme.Input("ملاحظات إضافية");
        var resultLbl = new Label { HorizontalTextAlignment = TextAlignment.Center, FontAttributes = FontAttributes.Bold };

        modePicker.SelectedIndexChanged += (s, e) => {
            if (modePicker.SelectedIndex == 0) {
                field1.Placeholder = "الموقع (مثال: الرف A1 / فرع عمان)";
                field2.Placeholder = "الكمية الملاحظة";
            } else {
                field1.Placeholder = "اسم المنافس (مثال: كارفور / سامح مول)";
                field2.Placeholder = "سعر المنافس";
            }
        };

        var saveBtn = Theme.Filled("تسجيل العملية");
        saveBtn.Clicked += (s, e) => {
            if (itPicker.SelectedIndex < 0 || string.IsNullOrWhiteSpace(field1.Text) || string.IsNullOrWhiteSpace(field2.Text)) {
                resultLbl.Text = "يرجى تعبئة جميع الحقول المطلوبة";
                return;
            }
            var itemId = _items[itPicker.SelectedIndex].Id;
            resultLbl.Text = "جاري الإرسال...";

            Task.Run(() => {
                try {
                    if (modePicker.SelectedIndex == 0) {
                        var checkId = ApiClient.CreateShelfCheckSync(itemId, field1.Text, field2.Text, notesField.Text ?? "", "");
                        MainThread.BeginInvokeOnMainThread(() => resultLbl.Text = "تم تسجيل فحص الرف بنجاح: " + checkId);
                    } else {
                        var capId = ApiClient.CreatePriceCaptureSync(itemId, field1.Text, field2.Text);
                        MainThread.BeginInvokeOnMainThread(() => resultLbl.Text = "تم رصد سعر المنافس بنجاح: " + capId);
                    }
                } catch (Exception ex) {
                    MainThread.BeginInvokeOnMainThread(() => resultLbl.Text = "خطأ: " + ex.Message);
                }
            });
        };

        root.Add(head);
        root.Add(Theme.CardView(modePicker, itPicker, scanBtn, field1, field2, notesField, saveBtn, resultLbl));
        Show(new ScrollView { Content = root });
    }
}
