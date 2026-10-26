using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace ERPSystem.Mobile;

/// <summary>
/// مخزن محلي أولاً (Local-First): كاش للقراءة دون اتصال + طابور مزامنة + آخر مزامنة.
/// ملف JSON واحد (erp_mobile_store.json) داخل مجلد بيانات التطبيق الآمن على كل المنصات
/// (Android: مجلد خاص بالتطبيق — Windows: %LOCALAPPDATA%).
/// </summary>
public static class LocalStore
{
    private static readonly string Db = Path.Combine(FileSystem.AppDataDirectory, "erp_mobile_store.json");
    private static readonly object _lock = new object();

    private static List<ItemDto> _items = new List<ItemDto>();
    private static List<CustomerDto> _customers = new List<CustomerDto>();
    private static List<SupplierDto> _suppliers = new List<SupplierDto>();
    private static List<WarehouseDto> _warehouses = new List<WarehouseDto>();
    private static List<OutboxItem> _outbox = new List<OutboxItem>();
    private static string _lastSync = "";

    public static void Reset()
    {
        lock (_lock)
        {
            _items = new List<ItemDto>();
            _customers = new List<CustomerDto>();
            _suppliers = new List<SupplierDto>();
            _warehouses = new List<WarehouseDto>();
            _outbox = new List<OutboxItem>();
            _lastSync = "";
            SaveInternal();
        }
    }

    public static string LastSync() { lock (_lock) { LoadInternal(); return _lastSync; } }
    public static void SetLastSyncNow() { lock (_lock) { _lastSync = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"); SaveInternal(); } }

    public static List<ItemDto> CachedItems() { lock (_lock) { LoadInternal(); return _items.ToList(); } }
    public static List<CustomerDto> CachedCustomers() { lock (_lock) { LoadInternal(); return _customers.ToList(); } }
    public static List<SupplierDto> CachedSuppliers() { lock (_lock) { LoadInternal(); return _suppliers.ToList(); } }
    public static List<WarehouseDto> CachedWarehouses() { lock (_lock) { LoadInternal(); return _warehouses.ToList(); } }

    public static void CacheAll(List<ItemDto> items, List<CustomerDto> customers, List<SupplierDto> suppliers, List<WarehouseDto> warehouses)
    {
        lock (_lock)
        {
            _items = items;
            _customers = customers;
            _suppliers = suppliers;
            _warehouses = warehouses;
            SaveInternal();
        }
    }

    public static void Enqueue(string key, string payload)
    {
        lock (_lock)
        {
            LoadInternal();
            _outbox.Add(new OutboxItem(key, payload, "Pending"));
            SaveInternal();
        }
    }

    public static List<OutboxItem> ListPending()
    {
        lock (_lock)
        {
            LoadInternal();
            var list = new List<OutboxItem>();
            foreach (var o in _outbox) if (o.Status == "Pending") list.Add(o);
            return list;
        }
    }

    public static int CountPending()
    {
        lock (_lock)
        {
            LoadInternal();
            var n = 0;
            foreach (var o in _outbox) if (o.Status == "Pending") n++;
            return n;
        }
    }

    public static void MarkStatus(string key, string status)
    {
        lock (_lock)
        {
            LoadInternal();
            var next = new List<OutboxItem>();
            foreach (var o in _outbox) next.Add(o.ClientKey == key ? new OutboxItem(o.ClientKey, o.Payload, status) : o);
            _outbox = next;
            SaveInternal();
        }
    }

// ══ القراءة والكتابة ══
    private static void LoadInternal()
    {
        try
        {
            if (!File.Exists(Db)) return;
            using var f = File.OpenRead(Db);
            using var rd = new StreamReader(f);
            var rootText = rd.ReadToEnd();
            if (string.IsNullOrWhiteSpace(rootText)) return;

            _lastSync = JsonLite.Unescape(JsonLite.Field(rootText, "lastSync"));

            _items = new List<ItemDto>();
            foreach (var n in JsonLite.Objects(JsonLite.ArrayField(rootText, "items")))
            {
                _items.Add(new ItemDto(
                    JsonLite.Field(n, "id"),
                    JsonLite.Field(n, "code"),
                    JsonLite.Unescape(JsonLite.Field(n, "nameAr")),
                    JsonLite.Field(n, "nameEn") == "" ? null : JsonLite.Unescape(JsonLite.Field(n, "nameEn")),
                    JsonLite.Field(n, "salePrice"),
                    JsonLite.Field(n, "currentStock"),
                    JsonLite.Field(n, "taxRate"),
                    JsonLite.Field(n, "barcode")));
            }

            _customers = new List<CustomerDto>();
            foreach (var n in JsonLite.Objects(JsonLite.ArrayField(rootText, "customers")))
            {
                _customers.Add(new CustomerDto(
                    JsonLite.Field(n, "id"),
                    JsonLite.Field(n, "code"),
                    JsonLite.Unescape(JsonLite.Field(n, "nameAr")),
                    JsonLite.Field(n, "phone") == "" ? null : JsonLite.Unescape(JsonLite.Field(n, "phone")),
                    JsonLite.Field(n, "currentBalance")));
            }

            _suppliers = new List<SupplierDto>();
            foreach (var n in JsonLite.Objects(JsonLite.ArrayField(rootText, "suppliers")))
            {
                _suppliers.Add(new SupplierDto(
                    JsonLite.Field(n, "id"),
                    JsonLite.Field(n, "code"),
                    JsonLite.Unescape(JsonLite.Field(n, "nameAr")),
                    JsonLite.Field(n, "phone") == "" ? null : JsonLite.Unescape(JsonLite.Field(n, "phone")),
                    JsonLite.Field(n, "currentBalance")));
            }

            _warehouses = new List<WarehouseDto>();
            foreach (var n in JsonLite.Objects(JsonLite.ArrayField(rootText, "warehouses")))
            {
                _warehouses.Add(new WarehouseDto(
                    JsonLite.Field(n, "id"),
                    JsonLite.Field(n, "code"),
                    JsonLite.Unescape(JsonLite.Field(n, "nameAr"))));
            }

            _outbox = new List<OutboxItem>();
            foreach (var n in JsonLite.Objects(JsonLite.ArrayField(rootText, "outbox")))
            {
                var payloadB64 = JsonLite.Field(n, "payload");
                var payload = "";
                try { payload = Text.Utf8Decode(Convert.FromBase64String(payloadB64)); } catch { }
                _outbox.Add(new OutboxItem(JsonLite.Field(n, "key"), payload, JsonLite.Field(n, "status")));
            }
        }
        catch { }
    }

    private static void SaveInternal()
    {
        var sb = new StringBuilder();
        sb.Append("{\"lastSync\":\"").Append(Text.Json(_lastSync)).Append("\",");
        AppendItems(sb); sb.Append(",");
        AppendCustomers(sb); sb.Append(",");
        AppendSuppliers(sb); sb.Append(",");
        AppendWarehouses(sb); sb.Append(",");
        AppendOutbox(sb);
        sb.Append("}");
        try
        {
            using var f = new FileStream(Db, FileMode.Create, FileAccess.Write, FileShare.None);
            f.Write(Text.Utf8Encode(sb.ToString()));
            f.Flush();
        }
        catch { }
    }

    private static void AppendItems(StringBuilder sb)
    {
        sb.Append("\"items\":[");
        var first = true;
        foreach (var it in _items)
        {
            if (!first) sb.Append(",");
            first = false;
            sb.Append("{\"id\":\"").Append(Text.Json(it.Id)).Append("\",\"code\":\"").Append(Text.Json(it.Code))
              .Append("\",\"nameAr\":\"").Append(Text.Json(it.NameAr)).Append("\",\"nameEn\":");
            sb.Append(it.NameEn is null ? "null" : "\"" + Text.Json(it.NameEn) + "\"");
            sb.Append(",\"salePrice\":\"").Append(Text.Json(it.SalePrice))
              .Append("\",\"currentStock\":\"").Append(Text.Json(it.CurrentStock))
              .Append("\",\"taxRate\":\"").Append(Text.Json(it.TaxRate))
              .Append("\",\"barcode\":").Append(it.Barcode is null ? "null" : "\"" + Text.Json(it.Barcode) + "\"")
              .Append("}");
        }
        sb.Append("]");
    }

    private static void AppendCustomers(StringBuilder sb)
    {
        sb.Append("\"customers\":[");
        var first = true;
        foreach (var c in _customers)
        {
            if (!first) sb.Append(",");
            first = false;
            sb.Append("{\"id\":\"").Append(Text.Json(c.Id)).Append("\",\"code\":\"").Append(Text.Json(c.Code))
              .Append("\",\"nameAr\":\"").Append(Text.Json(c.NameAr)).Append("\",\"phone\":")
              .Append(c.Phone is null ? "null" : "\"" + Text.Json(c.Phone) + "\"")
              .Append(",\"currentBalance\":\"").Append(Text.Json(c.CurrentBalance)).Append("\"}");
        }
        sb.Append("]");
    }

    private static void AppendSuppliers(StringBuilder sb)
    {
        sb.Append("\"suppliers\":[");
        var first = true;
        foreach (var s in _suppliers)
        {
            if (!first) sb.Append(",");
            first = false;
            sb.Append("{\"id\":\"").Append(Text.Json(s.Id)).Append("\",\"code\":\"").Append(Text.Json(s.Code))
              .Append("\",\"nameAr\":\"").Append(Text.Json(s.NameAr)).Append("\",\"phone\":")
              .Append(s.Phone is null ? "null" : "\"" + Text.Json(s.Phone) + "\"")
              .Append(",\"currentBalance\":\"").Append(Text.Json(s.CurrentBalance)).Append("\"}");
        }
        sb.Append("]");
    }

    private static void AppendWarehouses(StringBuilder sb)
    {
        sb.Append("\"warehouses\":[");
        var first = true;
        foreach (var w in _warehouses)
        {
            if (!first) sb.Append(",");
            first = false;
            sb.Append("{\"id\":\"").Append(Text.Json(w.Id)).Append("\",\"code\":\"").Append(Text.Json(w.Code))
              .Append("\",\"nameAr\":\"").Append(Text.Json(w.NameAr)).Append("\"}");
        }
        sb.Append("]");
    }

    private static void AppendOutbox(StringBuilder sb)
    {
        sb.Append("\"outbox\":[");
        var first = true;
        foreach (var o in _outbox)
        {
            if (!first) sb.Append(",");
            first = false;
            // الحمولة تُخزَّن Base64 (نص ASCII آمن — بلا تعشيش ترميز)
            var b64 = Convert.ToBase64String(Text.Utf8Encode(o.Payload));
            sb.Append("{\"key\":\"").Append(Text.Json(o.ClientKey))
              .Append("\",\"payload\":\"").Append(Text.Json(b64))
              .Append("\",\"status\":\"").Append(Text.Json(o.Status)).Append("\"}");
        }
        sb.Append("]");
    }
}