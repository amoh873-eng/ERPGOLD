using System.Net.Http;
using System.Text.Json;
using System.Text;

namespace ERPSystem.Mobile;

/// <summary>عميل REST لخادم ERP — استدعاءات async ونسخ متزامنة (للمعالجات اللحظية).</summary>
public static class ApiClient
{
    // ── الإصدارات غير المتزامنة (تُحظر في خيط الاستدعاء — للواجهة البسيطة) ──
    public static LoginResponseDto LoginSync(string email, string password)
        => Await(LoginAsync(email, password));

    public static List<ItemDto> ItemsSync(string search)
        => Await(ItemsAsync(search));

    public static List<CustomerDto> CustomersSync(string search)
        => Await(CustomersAsync(search));

    public static List<SupplierDto> SuppliersSync(string search)
        => Await(SuppliersAsync(search));

    public static List<WarehouseDto> WarehousesSync()
        => Await(WarehousesAsync());

    /// <summary>إنشاء فاتورة (متعدد الأصناف).</summary>
    public static InvoiceDto CreateInvoiceSync(List<InvoiceLineDto> lines, string customerId, string warehouseId, int paymentMethod, string note, string photoBase64 = "")
    {
        var key0 = Guid.NewGuid().ToString();
        var payload = MakeInvoicePayload(lines, customerId, warehouseId, note, key0, paymentMethod, photoBase64);
        var resp = Await(SendAsync("POST", "/api/sales-invoices", payload));
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        return ParseInvoice(resp.body);
    }

    /// <summary>نسخة سطر واحد (متوافقة مع الاختبارات) — تحوَّل إلى قائمة أسطر.</summary>
    public static InvoiceDto CreateInvoiceSync(string customerId, string warehouseId, string itemId, string quantity, string unitPrice, int paymentMethod, string note, string photoBase64 = "")
    {
        return CreateInvoiceSync(ListOf(itemId, quantity, unitPrice), customerId, warehouseId, paymentMethod, note, photoBase64);
    }

    private static List<InvoiceLineDto> ListOf(string itemId, string quantity, string unitPrice)
    {
        var l = new List<InvoiceLineDto>();
        l.Add(new InvoiceLineDto(itemId, quantity, unitPrice));
        return l;
    }

    /// <summary>حمولة طلب ميداني كاملة (أسطر متعددة + مفتاح تكرار + طريقة دفع + صورة اختيارية).</summary>
    public static string MakeInvoicePayload(List<InvoiceLineDto> lines, string customerId, string warehouseId, string note, string clientKey, int paymentMethod = 1, string photoBase64 = "")
    {
        var body = "{"
            + "\"warehouseId\":\"" + warehouseId + "\","
            + "\"customerId\":\"" + customerId + "\","
            + "\"paymentMethod\":" + paymentMethod + ","
            + "\"lines\":[";
        var first = true;
        foreach (var ln in lines)
        {
            if (!first) body += ",";
            first = false;

            var qtyVal = RootPage.ParseDecimal(ln.Quantity, 1m);
            var priceVal = RootPage.ParseDecimal(ln.UnitPrice, 0m);

            body += "{\"itemId\":\"" + ln.ItemId
                + "\",\"quantity\":" + qtyVal.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"unitPrice\":" + priceVal.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }
        body += "],"
            + "\"note\":\"" + Text.Json(note) + "\","
            + "\"clientRequestId\":\"" + Text.Json(clientKey) + "\"";
        if (!string.IsNullOrEmpty(photoBase64)) body += ",\"photoBase64\":\"" + photoBase64 + "\"";
        body += "}";
        return body;
    }

    /// <summary>نسخة سطر واحد — تحوَّل إلى قائمة أسطر.</summary>
    public static string MakeInvoicePayload(string customerId, string warehouseId, string itemId, string quantity, string unitPrice, string note, string clientKey, int paymentMethod = 1, string photoBase64 = "")
    {
        return MakeInvoicePayload(ListOf(itemId, quantity, unitPrice), customerId, warehouseId, note, clientKey, paymentMethod, photoBase64);
    }

    /// <summary>نتيجة إرسال عنصر طابور: (نجاح، يحتاج مراجعة، مكرر، رقم الفاتورة، رسالة).</summary>
    public static (bool ok, bool needsReview, bool duplicate, string number, string message) SendInvoiceFromQueueSync(string payload)
    {
        var resp = Await(SendAsync("POST", "/api/sales-invoices", payload));
        if (!resp.ok) return (false, false, false, "", resp.body);
        var body = resp.body;
        var needs = JsonLite.Field(body, "needsReview") == "true";
        var dup = JsonLite.Field(body, "duplicate") == "true";
        var number = JsonLite.Unescape(JsonLite.Field(body, "invoiceNumber"));
        return (true, needs, dup, number, body);
    }

    /// <summary>تحصيل ميداني في عهدة المندوب (المستخدم الحالي هو المندوب).</summary>
    public static string CollectCustodySync(string customerId, string amount, string note)
    {
        var body = "{\"customerId\":\"" + Text.Json(customerId)
            + "\",\"amount\":" + amount
            + ",\"note\":\"" + Text.Json(note) + "\",\"paymentMethod\":1}";
        var resp = Await(SendAsync("POST", "/api/reps/custody", body));
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        return JsonLite.Unescape(JsonLite.Field(resp.body, "entryNumber"));
    }

    /// <summary>البحث عن فاتورة مرحلة للمردود مع الأسطر القابلة للرد.</summary>
    public static ReturnLookupDto ReturnLookupSync(string invoiceNumber)
    {
        var body = Await(GetBodyAsync("/api/sales-returns/lookup?invoiceNumber=" + invoiceNumber.Trim()));
        var lines = new List<ReturnLookupLineDto>();
        foreach (var n in JsonLite.Objects(JsonLite.ArrayField(body, "lines")))
            lines.Add(new ReturnLookupLineDto(
                JsonLite.Field(n, "itemId"),
                JsonLite.Field(n, "code"),
                JsonLite.Unescape(JsonLite.Field(n, "nameAr")),
                JsonLite.Field(n, "quantity"),
                JsonLite.Field(n, "unitPrice"),
                JsonLite.Field(n, "remaining")));
        return new ReturnLookupDto(
            JsonLite.Field(body, "invoiceId"),
            JsonLite.Unescape(JsonLite.Field(body, "invoiceNumber")),
            JsonLite.Field(body, "customerId"),
            JsonLite.Unescape(JsonLite.Field(body, "customerName")),
            JsonLite.Field(body, "warehouseId"),
            JsonLite.Unescape(JsonLite.Field(body, "warehouseName")),
            JsonLite.Field(body, "invoiceDate"),
            JsonLite.Field(body, "totalAmount"),
            lines);
    }

    /// <summary>إنشاء مردود مبيعات (يعتمد أسعار الفاتورة الأصلية).</summary>
    public static ReturnDto CreateSalesReturnSync(string salesInvoiceId, string warehouseId, List<CreateReturnLineDto> lines, string note)
    {
        var payload = "{\"salesInvoiceId\":\"" + salesInvoiceId
            + "\",\"warehouseId\":\"" + warehouseId
            + "\",\"note\":\"" + Text.Json(note) + "\",\"lines\":[";
        var first = true;
        foreach (var ln in lines)
        {
            if (!first) payload += ",";
            first = false;
            payload += "{\"itemId\":\"" + ln.ItemId + "\",\"quantity\":" + ln.Quantity + "}";
        }
        payload += "]}";
        var resp = Await(SendAsync("POST", "/api/sales-returns", payload));
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        return new ReturnDto(
            JsonLite.Field(resp.body, "id"),
            JsonLite.Unescape(JsonLite.Field(resp.body, "returnNumber")),
            JsonLite.Unescape(JsonLite.Field(resp.body, "invoiceNumber")),
            JsonLite.Field(resp.body, "totalAmount"));
    }

    /// <summary>الحمولة مبنية للاختبار المباشر للتحصيل.</summary>
    public static string MakeCustodyPayload(string customerId, string amount, string note)
        => "{\"customerId\":\"" + Text.Json(customerId)
            + "\",\"amount\":" + amount
            + ",\"note\":\"" + Text.Json(note) + "\",\"paymentMethod\":1}";

    /// <summary>حمولة محلية كاملة للفحص + إرسال فور (يعيد مسار الصورة).</summary>
    public static string CreateShelfCheckSync(string itemId, string location, string qty, string notes, string photoBase64)
    {
        var body = "{\"itemId\":\"" + Text.Json(itemId)
            + "\",\"location\":\"" + Text.Json(location)
            + "\",\"observedQty\":" + qty
            + ",\"notes\":\"" + Text.Json(notes) + "\"";
        if (!string.IsNullOrEmpty(photoBase64)) body += ",\"photoOne\":\"" + photoBase64 + "\"";
        body += "}";
        var resp = Await(SendAsync("POST", "/api/shelf/checks", body));
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        return JsonLite.Field(resp.body, "id");
    }

    /// <summary>إرسال رصد سعر منافس (يعيد المعرف).</summary>
    public static string CreatePriceCaptureSync(string itemId, string competitor, string price)
    {
        var body = "{\"itemId\":\"" + Text.Json(itemId)
            + "\",\"competitorName\":\"" + Text.Json(competitor)
            + "\",\"price\":" + price + "}";
        var resp = Await(SendAsync("POST", "/api/shelf/price-captures", body));
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        return JsonLite.Field(resp.body, "id");
    }

    /// <summary>قراءة حدود الرفوف (JSON خام للعرض).</summary>
    public static string ParLevelsSync()
    {
        var resp = Await(SendAsync("GET", "/api/shelf/par-levels", null));
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        return resp.body;
    }

    /// <summary>قراءة تحذيرات الانتهاء (JSON خام للعرض).</summary>
    public static string ExpiryWarningsSync()
    {
        var resp = Await(SendAsync("GET", "/api/shelf/expiry-warnings", null));
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        return resp.body;
    }

    public static AccountStatementDto GetCustomerStatementSync(string id, DateTime from, DateTime to)
    {
        var url = $"/api/customers/{id}/statement?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
        return ParseStatement(Await(GetBodyAsync(url)), "customerName");
    }

    public static AccountStatementDto GetSupplierStatementSync(string id, DateTime from, DateTime to)
    {
        var url = $"/api/suppliers/{id}/statement?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
        return ParseStatement(Await(GetBodyAsync(url)), "supplierName");
    }

    private static AccountStatementDto ParseStatement(string body, string nameField)
    {
        var lines = new List<StatementLineDto>();
        foreach (var n in JsonLite.Objects(JsonLite.ArrayField(body, "lines")))
        {
            lines.Add(new StatementLineDto(
                JsonLite.Field(n, "date"),
                JsonLite.Unescape(JsonLite.Field(n, "documentNumber")),
                JsonLite.Unescape(JsonLite.Field(n, "typeNameAr")),
                JsonLite.Field(n, "debit"),
                JsonLite.Field(n, "credit"),
                JsonLite.Field(n, "balance")));
        }
        return new AccountStatementDto(
            JsonLite.Unescape(JsonLite.Field(body, nameField)),
            JsonLite.Field(body, "from"),
            JsonLite.Field(body, "to"),
            JsonLite.Field(body, "totalDebit"),
            JsonLite.Field(body, "totalCredit"),
            JsonLite.Field(body, "closingBalance"),
            lines);
    }

    public static string CreatePurchaseInvoiceSync(string supplierId, string whId, string note, List<InvoiceLineDto> lines)
    {
        var sb = new StringBuilder();
        sb.Append("{\"supplierId\":\"").Append(supplierId).Append("\",\"warehouseId\":\"").Append(whId).Append("\",\"note\":\"").Append(Text.Json(note)).Append("\",\"lines\":[");
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"itemId\":\"").Append(lines[i].ItemId).Append("\",\"quantity\":").Append(lines[i].Quantity).Append(",\"unitCost\":").Append(lines[i].UnitPrice).Append("}");
        }
        sb.Append("]}");
        var resp = Await(SendAsync("POST", "/api/purchase-invoices", sb.ToString()));
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        return resp.body;
    }

    public static List<InvoiceHeaderDto> SearchInvoicesSync(string term)
    {
        var body = Await(GetBodyAsync("/api/sales-invoices/search?term=" + term.Trim()));
        var list = new List<InvoiceHeaderDto>();
        foreach (var n in JsonLite.Objects(body))
        {
            list.Add(new InvoiceHeaderDto(
                JsonLite.Field(n, "id"),
                JsonLite.Unescape(JsonLite.Field(n, "invoiceNumber")),
                JsonLite.Unescape(JsonLite.Field(n, "customerName")),
                JsonLite.Field(n, "invoiceDate"),
                JsonLite.Field(n, "totalAmount")));
        }
        return list;
    }

    private static T Await<T>(Task<T> f) => f.GetAwaiter().GetResult();

    // ── الإصدارات اللاتزامنية ──
    public static async Task<LoginResponseDto> LoginAsync(string email, string password)
    {
        var body = "{\"email\":\"" + Text.Json(email) + "\",\"password\":\"" + Text.Json(password) + "\"}";
        var resp = await SendAsync("POST", "/api/auth/login", body);
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        var token = JsonLite.Field(resp.body, "token");
        var userBlock = JsonLite.ObjectField(resp.body, "user");
        var userEmail = userBlock == "" ? "" : JsonLite.Unescape(JsonLite.Field(userBlock, "email"));
        var rolesRaw = JsonLite.ArrayField(userBlock, "roles").Replace("[", "").Replace("]", "").Replace("\"", "");
        return new LoginResponseDto(token, userEmail, rolesRaw);
    }

    public static async Task<List<ItemDto>> ItemsAsync(string search)
    {
        var body = await GetBodyAsync("/api/items");
        var list = new List<ItemDto>();
        foreach (var n in JsonLite.Objects(body))
        {
            list.Add(new ItemDto(
                JsonLite.Field(n, "id"),
                JsonLite.Field(n, "code"),
                JsonLite.Unescape(JsonLite.Field(n, "nameAr")),
                JsonLite.Field(n, "nameEn") == "" ? null : JsonLite.Unescape(JsonLite.Field(n, "nameEn")),
                JsonLite.Field(n, "salePrice"),
                JsonLite.Field(n, "currentStock"),
                JsonLite.Field(n, "taxRate"),
                JsonLite.Field(n, "barcode")));
        }
        return list;
    }

    public static async Task<List<CustomerDto>> CustomersAsync(string search)
    {
        var body = await GetBodyAsync("/api/customers");
        var list = new List<CustomerDto>();
        foreach (var n in JsonLite.Objects(body))
        {
            list.Add(new CustomerDto(
                JsonLite.Field(n, "id"),
                JsonLite.Field(n, "code"),
                JsonLite.Unescape(JsonLite.Field(n, "nameAr")),
                JsonLite.Field(n, "phone") == "" ? null : JsonLite.Unescape(JsonLite.Field(n, "phone")),
                JsonLite.Field(n, "currentBalance")));
        }
        return list;
    }

    public static async Task<List<SupplierDto>> SuppliersAsync(string search)
    {
        var body = await GetBodyAsync("/api/suppliers");
        var list = new List<SupplierDto>();
        foreach (var n in JsonLite.Objects(body))
        {
            list.Add(new SupplierDto(
                JsonLite.Field(n, "id"),
                JsonLite.Field(n, "code"),
                JsonLite.Unescape(JsonLite.Field(n, "nameAr")),
                JsonLite.Field(n, "phone") == "" ? null : JsonLite.Unescape(JsonLite.Field(n, "phone")),
                JsonLite.Field(n, "currentBalance")));
        }
        return list;
    }

    public static async Task<List<WarehouseDto>> WarehousesAsync()
    {
        var body = await GetBodyAsync("/api/warehouses");
        var list = new List<WarehouseDto>();
        foreach (var n in JsonLite.Objects(body))
        {
            list.Add(new WarehouseDto(
                JsonLite.Field(n, "id"),
                JsonLite.Field(n, "code"),
                JsonLite.Unescape(JsonLite.Field(n, "nameAr"))));
        }
        return list;
    }

    public static async Task<InvoiceDto> CreateInvoiceAsync(string customerId, string warehouseId, string itemId, string quantity, string unitPrice)
    {
        var body = MakeInvoicePayload(customerId, warehouseId, itemId, quantity, unitPrice, "Mobile", "");
        var resp = await SendAsync("POST", "/api/sales-invoices", body);
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        return ParseInvoice(resp.body);
    }

    private static InvoiceDto ParseInvoice(string json)
    {
        return new InvoiceDto(
            JsonLite.Field(json, "id"),
            JsonLite.Unescape(JsonLite.Field(json, "invoiceNumber")),
            JsonLite.Field(json, "totalAmount"),
            JsonLite.Unescape(JsonLite.Field(json, "note")));
    }

    /// <summary>اختبار الاتصال بالخادم والـ API ومسار المصادقة.</summary>
    public static async Task<(bool serverOk, bool apiOk, bool authOk, string message)> TestConnectionAsync(string baseUrl)
    {
        var cleanBaseUrl = baseUrl.TrimEnd('/');
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(6);

        bool serverOk = false;
        bool apiOk = false;
        bool authOk = false;

        // 1. اختبار وصول الخادم
        try
        {
            var pingReq = new HttpRequestMessage(HttpMethod.Get, cleanBaseUrl);
            var pingResp = await http.SendAsync(pingReq);
            serverOk = true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            serverOk = true;
        }
        catch
        {
            serverOk = false;
        }

        // 2. اختبار الـ API (/api/items أو /api/warehouses)
        try
        {
            var apiReq = new HttpRequestMessage(HttpMethod.Get, cleanBaseUrl + "/api/warehouses");
            var apiResp = await http.SendAsync(apiReq);
            if (apiResp.IsSuccessStatusCode || apiResp.StatusCode == System.Net.HttpStatusCode.Unauthorized || apiResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                apiOk = true;
            }
        }
        catch { }

        // 3. اختبار مسار المصادقة (/api/auth/login)
        try
        {
            var authBody = new StringContent("{}", Encoding.UTF8, "application/json");
            var authReq = new HttpRequestMessage(HttpMethod.Post, cleanBaseUrl + "/api/auth/login") { Content = authBody };
            var authResp = await http.SendAsync(authReq);
            if (authResp.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                authResp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                authResp.StatusCode == System.Net.HttpStatusCode.OK ||
                authResp.IsSuccessStatusCode)
            {
                authOk = true;
            }
        }
        catch { }

        var sb = new StringBuilder();
        sb.AppendLine(serverOk ? "✓ الخادم قابل للوصول" : "❌ الخادم غير قابل للوصول");
        sb.AppendLine(apiOk ? "✓ API يعمل" : "❌ API لا يعمل");
        sb.AppendLine(authOk ? "✓ Authentication endpoint يعمل" : "❌ Authentication endpoint لا يعمل");

        return (serverOk, apiOk, authOk, sb.ToString().TrimEnd());
    }

    private static async Task<string> GetBodyAsync(string path)
    {
        var resp = await SendAsync("GET", path, null);
        if (!resp.ok) throw new InvalidOperationException(resp.body);
        return resp.body;
    }

    private static async Task<(bool ok, string body)> SendAsync(string method, string path, string? jsonBody)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(8); // حل جذري: لا تترك التطبيق معلقاً للأبد
        var req = new HttpRequestMessage(new HttpMethod(method), Session.BaseUrl + path);
        if (!string.IsNullOrEmpty(Session.Token)) req.Headers.Add("Authorization", "Bearer " + Session.Token);
        if (jsonBody is not null)
        {
            var content = new ByteArrayContent(Text.AsciiBytes(jsonBody));
            content.Headers.Add("Content-Type", "application/json");
            req.Content = content;
        }
        try
        {
            var resp = await http.SendAsync(req);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return (resp.IsSuccessStatusCode, Text.Utf8Decode(bytes));
        }
        catch (Exception ex)
        {
            return (false, "{\"error\":\"" + Text.Json(ex.Message) + "\"}");
        }
    }
}