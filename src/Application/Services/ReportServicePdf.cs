using ERPSystem.Application.DTOs.Reports;
namespace ERPSystem.Application.Services;
public partial class ReportService
{
    public byte[] ExportReportExcel(GenericReportTable t)
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add("Report");

        // ── ترويسة التقرير الهرمي (ميزان المراجعة حسب المستويات): الاسم والفترة أعلى الملف ──
        // محصورة في التقارير التي تُعلن مجموعات أعمدة (HeaderGroups) ⇒ بقية التقارير كما هي بلا تغيير.
        var hasGroups = t.HeaderGroups != null && t.HeaderGroups.Count > 0;
        int row = 1;
        if (hasGroups)
        {
            ws.Cell(row, 1).Value = t.TitleAr;
            ws.Range(row, 1, row, Math.Max(1, t.Headers.Count)).Merge();
            ws.Row(row).Style.Font.Bold = true;
            ws.Row(row).Style.Font.FontSize = 12;
            row++;
            ws.Cell(row, 1).Value = t.ParamSummary;
            ws.Range(row, 1, row, Math.Max(1, t.Headers.Count)).Merge();
            row += 2; // سطر فاصل قبل الجدول
        }

        // ── صف رأس المجموعات (اختياري للتقارير الهرمية) ──
        int headerRow = row;
        if (hasGroups)
        {
            int col = 1;
            foreach (var g in t.HeaderGroups!)
            {
                var span = g.Span < 1 ? 1 : g.Span;
                ws.Cell(headerRow, col).Value = g.Title;
                if (span > 1) ws.Range(headerRow, col, headerRow, col + span - 1).Merge();
                col += span;
            }
            ws.Row(headerRow).Style.Font.Bold = true;
            headerRow++;
        }

        for(int i=0;i<t.Headers.Count;i++) ws.Cell(headerRow,i+1).Value = t.Headers[i];
        ws.Row(headerRow).Style.Font.Bold = true;

        int dataStart = headerRow + 1;
        for(int r=0;r<t.Rows.Count;r++) for(int c=0;c<t.Headers.Count;c++) ws.Cell(dataStart+r,c+1).Value = t.Rows[r].ElementAtOrDefault(c)??"";
        if (t.Rows.Count > 0) ws.SheetView.FreezeRows(dataStart - 1);

        // ── الإجماليات: صفوف متعددة إن وُجدت، وإلا الصف الواحد التقليدي ──
        var totalsRows = t.TotalsRows != null && t.TotalsRows.Count > 0
            ? t.TotalsRows
            : (t.Totals != null ? new List<List<string>> { t.Totals } : null);
        if (totalsRows != null)
        {
            var rr = dataStart + t.Rows.Count;
            foreach (var totalRow in totalsRows)
            {
                for (int c = 0; c < totalRow.Count; c++) { ws.Cell(rr, c + 1).Value = totalRow[c]; ws.Cell(rr, c + 1).Style.Font.Bold = true; }
                rr++;
            }
        }

        // ── ملاحظة التقرير (FooterNote): تُكتب في Excel كما في الشاشة وPDF ──
        // مثال: تحذير وجود حركات مباشرة على حسابات تجميعية. لا تُضاف إلا إن وُجدت ⇒ بقية التقارير كما هي.
        if (!string.IsNullOrWhiteSpace(t.FooterNote))
        {
            var noteRow = dataStart + t.Rows.Count + (totalsRows?.Count ?? 0);
            ws.Cell(noteRow, 1).Value = t.FooterNote;
            if (t.Headers.Count > 1) ws.Range(noteRow, 1, noteRow, t.Headers.Count).Merge();
            ws.Row(noteRow).Style.Font.Italic = true;
            ws.Row(noteRow).Style.Font.FontSize = 9;
        }

        ws.Columns().AdjustToContents();
        using var ms=new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }
}
