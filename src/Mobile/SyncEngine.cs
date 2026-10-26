using System.Text;

namespace ERPSystem.Mobile;

public static class SyncEngine
{
    private static bool _started = false;

    public static void StartBackground()
    {
        if (_started) return;
        _started = true;
        var t = new Thread(() => Loop());
        t.IsBackground = true;
        t.Start();
    }

    private static void Loop()
    {
        while (true)
        {
            try { RunOnce(); }
            catch { }
            Thread.Sleep(30000);
        }
    }

    public static bool IsOnline()
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(3);
            var task = http.GetAsync(Session.BaseUrl + "/health");
            if (task.Wait(3000)) return task.Result.IsSuccessStatusCode;
            return false;
        }
        catch { return false; }
    }

    public static void RunOnce()
    {
        var pending = LocalStore.ListPending();
        foreach (var item in pending)
        {
            try
            {
                var r = ApiClient.SendInvoiceFromQueueSync(item.Payload);
                if (r.ok || r.needsReview || r.duplicate)
                {
                    LocalStore.MarkStatus(item.ClientKey, r.needsReview ? "NeedsReview" : "Synced");
                }
            }
            catch { }
        }
    }
}
