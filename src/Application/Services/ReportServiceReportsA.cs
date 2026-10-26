using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.DTOs.Reports;
using Microsoft.EntityFrameworkCore;
namespace ERPSystem.Application.Services;
public partial class ReportService
{
    private DateTime P(string? v, DateTime d) => DateTime.TryParse(v, out var x) ? x : d;
    private Guid G(string? v) => Guid.TryParse(v, out var g) ? g : Guid.Empty;
    public async Task<GenericReportTable> GetReportAsync(string key, Dictionary<string,string?> pars)
    {
        var from = P(pars.TryGetValue("from", out var f)?f:null, DateTime.Today.AddMonths(-1));
        var to = P(pars.TryGetValue("to", out var t)?t:null, DateTime.Today);
        var asOf = P(pars.TryGetValue("asOf", out var a)?a:null, DateTime.Today);
        GenericReportTable R(string ta,List<string> h,List<List<string>> rows,List<string>? tot=null,string? foot=null,bool land=true)
            => new(){ TitleAr=ta, Headers=h, Rows=rows, Totals=tot, FooterNote=foot, Landscape=land, ParamSummary=key.Contains("balance")?$"حتى {asOf:yyyy-MM-dd}":$"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}"};
        switch(key)
        {
            case "trial-balance":
            {
                // ميزان المراجعة الهرمي حسب مستويات دليل الحسابات (أب/ابن/حفيد/… ديناميكيًا).
                // المصدر: نفس محرّك الأرصدة الموحّد (AccountBalanceService) — بلا منطق أرصدة مكرر.
                DateTime? tbFrom = pars.TryGetValue("from", out var tbf) && !string.IsNullOrWhiteSpace(tbf) ? from : (DateTime?)null;
                DateTime? tbTo = pars.TryGetValue("to", out var tbt) && !string.IsNullOrWhiteSpace(tbt) ? to : (DateTime?)null;
                var tbRoot = G(pars.TryGetValue("accountId", out var tba) ? tba : null);
                return await GetTrialBalanceReportAsync(
                    tbFrom, tbTo,
                    tbRoot == Guid.Empty ? null : tbRoot,
                    BoolParam(pars, "includeZero", false),
                    BoolParam(pars, "levelsOnly", false));
            }
            case "accounts-tree-balances":
            {
                // تقرير شجرة الحسابات والأرصدة حسب المستويات (أب/ابن/حفيد).
                // المصدر الموحّد: نفس خدمة أرصدة الشجرة المستخدمة في شاشة الحسابات (لا منطق مكرر).
                DateTime? qFrom = pars.TryGetValue("from", out var fv) && !string.IsNullOrWhiteSpace(fv) ? from : (DateTime?)null;
                DateTime? qTo = pars.TryGetValue("to", out var tv) && !string.IsNullOrWhiteSpace(tv) ? to : (DateTime?)null;
                var rootId = G(pars.TryGetValue("accountId", out var av) ? av : null);

                var tree = await new AccountBalanceService(_context).GetBalanceTreeAsync(new AccountBalanceQuery
                {
                    From = qFrom,
                    To = qTo,
                    RootAccountId = rootId == Guid.Empty ? null : rootId
                });

                string Indent(AccountBalanceNodeDto n) => new string('\u00A0', Math.Max(0, n.Level - 1) * 3);
                var rows = tree.Flattened.Select(n => new List<string>
                {
                    Indent(n) + n.Code,
                    Indent(n) + n.NameAr,
                    n.LevelNameAr,
                    n.ParentAccountCode is null ? "—" : n.ParentAccountCode + " - " + (n.ParentAccountNameAr ?? ""),
                    n.OpeningBalance.ToString("N2"),
                    n.PeriodDebit.ToString("N2"),
                    n.PeriodCredit.ToString("N2"),
                    n.DebitBalance.ToString("N2"),
                    n.CreditBalance.ToString("N2"),
                    n.NetBalance.ToString("N2"),
                    n.NetNatureAr
                }).ToList();

                // الإجماليات من الجذور فقط — الأبناء والأحفاد داخل أرصدة آبائهم (لا تجميع مزدوج).
                var roots = tree.Roots;
                var note = "رصيد الحساب الأب = حركته المباشرة + مجموع أرصدة أبنائه (كل بند قيد يُحتسب مرة واحدة). "
                           + $"الإجمالي من الجذور (المستوى الأول) فقط. مدين {tree.TotalDebit:N2} = دائن {tree.TotalCredit:N2}"
                           + (tree.IsBalanced ? " — متوازن" : " — غير متوازن!");
                if (tree.AggregateAccountsWithDirectMovements.Count > 0)
                    note += " | تنبيه: حركة مباشرة على حسابات تجميعية: "
                            + string.Join(", ", tree.AggregateAccountsWithDirectMovements);

                return new GenericReportTable
                {
                    TitleAr = "تقرير شجرة الحسابات والأرصدة حسب المستويات",
                    Headers = new() { "رقم الحساب", "اسم الحساب", "المستوى", "الحساب الأب", "رصيد افتتاحي", "مدين (الفترة)", "دائن (الفترة)", "رصيد مدين", "رصيد دائن", "صافي الرصيد", "الطبيعة" },
                    Rows = rows,
                    Totals = new()
                    {
                        "", "الإجمالي (الجذور)", "", "",
                        roots.Sum(n => n.OpeningBalance).ToString("N2"),
                        roots.Sum(n => n.PeriodDebit).ToString("N2"),
                        roots.Sum(n => n.PeriodCredit).ToString("N2"),
                        roots.Sum(n => n.DebitBalance).ToString("N2"),
                        roots.Sum(n => n.CreditBalance).ToString("N2"),
                        roots.Sum(n => n.NetBalance).ToString("N2"),
                        ""
                    },
                    FooterNote = note,
                    ParamSummary = tree.HasPeriod
                        ? $"{tree.From:yyyy-MM-dd} → {tree.To:yyyy-MM-dd}"
                        : (tree.To.HasValue ? $"حتى {tree.To:yyyy-MM-dd}" : "كامل السجل"),
                    Landscape = true
                };
            }
            case "income-statement": { var d=await GetIncomeStatementAsync(from,to); var rows=new List<List<string>>(); rows.AddRange(d.Revenues.Select(x=>new List<string>{x.AccountCode,x.AccountNameAr,x.Amount.ToString("N2")})); rows.AddRange(d.Expenses.Select(x=>new List<string>{x.AccountCode,x.AccountNameAr,x.Amount.ToString("N2")})); return R("قائمة الدخل",new(){ "الرمز","الحساب","المبلغ"},rows,new(){ "","صافي الدخل",d.NetIncome.ToString("N2")}); }
            case "balance-sheet": { var d=await GetBalanceSheetAsync(asOf); var rows=new List<List<string>>(); rows.AddRange(d.Assets.Select(x=>new List<string>{x.AccountCode,x.AccountNameAr,x.Amount.ToString("N2")})); rows.AddRange(d.Liabilities.Select(x=>new List<string>{x.AccountCode,x.AccountNameAr,x.Amount.ToString("N2")})); rows.AddRange(d.Equity.Select(x=>new List<string>{x.AccountCode,x.AccountNameAr,x.Amount.ToString("N2")})); return R("الميزانية العمومية",new(){ "الرمز","الحساب","المبلغ"},rows,new(){ "","الأصول",d.TotalAssets.ToString("N2")},land:false); }
            case "general-ledger": { var aid=G(pars.TryGetValue("accountId",out var av)?av:null); if(aid==Guid.Empty) aid=await _context.Set<ERPSystem.Domain.Entities.Account>().Select(a=>a.Id).FirstOrDefaultAsync(); var led=await GetAccountLedgerAsync(aid,from,to); var rows=led.Lines.Select(l=>new List<string>{l.Date.ToString("yyyy-MM-dd"),l.EntryNumber,l.Description??"",l.Debit.ToString("N2"),l.Credit.ToString("N2"),l.Balance.ToString("N2")}).ToList(); return R("دفتر الأستاذ — "+led.AccountNameAr,new(){ "التاريخ","القيد","البيان","مدين","دائن","رصيد"},rows,new(){ "","","الرصيد",led.OpeningBalance.ToString("N2"),"",led.ClosingBalance.ToString("N2")}); }
            case "journal-register": { var ls=await _context.Set<ERPSystem.Domain.Entities.JournalEntryLine>().Include(l=>l.Account).Include(l=>l.JournalEntry).Where(l=>l.JournalEntry!=null && l.JournalEntry.EntryDate>=from.Date && l.JournalEntry.EntryDate<to.Date.AddDays(1)).OrderBy(l=>l.JournalEntry!.EntryDate).Take(500).ToListAsync(); var rows=ls.Select(l=>new List<string>{l.JournalEntry!.EntryDate.ToString("yyyy-MM-dd"),l.JournalEntry.EntryNumber,l.Account?.NameAr??"",l.DebitAmount.ToString("N2"),l.CreditAmount.ToString("N2"),l.JournalEntry.ReferenceNumber??""}).ToList(); return R("سجل القيود",new(){ "التاريخ","القيد","الحساب","مدين","دائن","مرجع"},rows); }
            case "cash-flow": { var d=await GetCashFlowStatementAsync(from,to); var op=d.Sections.First(s=>s.Key=="operating"); var rows=new List<List<string>>{ new(){ "صافي الدخل", d.Sections.Where(s=>s.Key=="operating").SelectMany(s=>s.Lines).Where(l=>l.DescriptionKey=="CashFlowNetIncome").Sum(l=>l.Amount).ToString("N2")} }; foreach(var l in op.Lines.Skip(1)) rows.Add(new List<string>{ l.DescriptionKey switch { "CashFlowAdjAR"=>"(زيادة)/نقصان الذمم المدينة", "CashFlowAdjInv"=>"(زيادة)/نقصان المخزون", "CashFlowAdjAP"=>"زيادة/(نقصان) الذمم الدائنة", "CashFlowAdjOtherLiab"=>"زيادة/(نقصان) الخصوم المتداولة الأخرى", _=>l.DescriptionKey }, l.Amount.ToString("N2")}); rows.Add(new List<string>{"الإجمالي الفرعي التشغيلي", op.Subtotal.ToString("N2")}); rows.Add(new List<string>{"صافي التغيير النقدي", d.NetChangeCash.ToString("N2")}); rows.Add(new List<string>{"الرصيد الافتتاحي", d.BeginningCash.ToString("N2")}); rows.Add(new List<string>{"الرصيد الختامي", d.EndingCash.ToString("N2")}); return R("قائمة التدفق النقدي",new(){ "البند","المبلغ"},rows,null,"الطريقة غير المباشرة — "+(d.IsBalanced?"متوازن":"غير متوازن"),true); }
            case "item-profitability": { var d=await GetItemProfitabilityAsync(from,to); var rows=d.Rows.Select(r=>new List<string>{r.ItemCode,r.ItemNameAr,r.CategoryNameAr,r.QtySold.ToString("N2"),r.Revenue.ToString("N2"),r.Cost.ToString("N2"),r.Margin.ToString("N2"),r.MarginPct.ToString("0.0")+"%"}).ToList(); return R("ربحية الأصناف",new(){ "الرمز","الصنف","الفئة","الكمية","الإيراد","التكلفة","هامش المساهمة","هامش%"},rows,null,$"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}",true); }
            case "slow-moving-stock": { var d=await GetSlowMovingAsync(int.TryParse(pars.TryGetValue("days",out var dv)?dv:null,out var dn)?dn:90); var rows=d.Rows.Select(r=>new List<string>{r.ItemCode,r.ItemNameAr,r.CurrentStock.ToString("N2"),r.StockValue.ToString("N2"),r.NeverSold?"لم يُبع":(r.DaysSinceLastSale?.ToString()??"—")}).ToList(); return R("بطيء الحركة / خامل",new(){ "الرمز","الصنف","كمية المخزون","قيمة المخزون","أيام منذ آخر بيع"},rows,null,$"آخر {d.DaysThreshold} يوم",true); }
            case "abc-analysis": { var d=await GetAbcAnalysisAsync(); var rows=d.Rows.Select(r=>new List<string>{r.ItemCode,r.ItemNameAr,r.Value.ToString("N2"),r.SharePct.ToString("0.0")+"%",r.CumulativePct.ToString("0.0")+"%",r.Tier}).ToList(); return R("تصنيف ABC",new(){ "الرمز","الصنف","قيمة المخزون","النسبة","النسبة التراكمية","الفئة"},rows,new(){ "","الإجمالي",d.TotalValue.ToString("N2"),"100%","",""}); }
            case "seasonality": { var d=await GetSeasonalityAsync(); var rows=d.Series.Select(s=>s.Months.Select(m=>m.ToString("N0")).Prepend(s.Year.ToString()).Append(s.Total.ToString("N2")).ToList()).ToList(); return R("الموسمية",new List<string>{ "السنة" }.Concat(Enumerable.Range(1,12).Select(x=>x.ToString())).Append("الإجمالي").ToList(),rows,null,$"الإجمالي الكلي: {d.GrandTotal:N2}",true); }
            case "vat-summary": { var sales=await _context.Set<ERPSystem.Domain.Entities.SalesInvoice>().Where(s=>!s.IsDeleted && s.InvoiceDate>=from.Date && s.InvoiceDate<to.Date.AddDays(1)).ToListAsync(); var purch=await _context.Set<ERPSystem.Domain.Entities.PurchaseInvoice>().Where(s=>!s.IsDeleted && s.InvoiceDate>=from.Date && s.InvoiceDate<to.Date.AddDays(1)).ToListAsync(); decimal stax=sales.Sum(s=>s.TaxAmount), ptax=purch.Sum(s=>s.TaxAmount); var rows=new List<List<string>>{ new(){ "إجمالي المبيعات",sales.Sum(s=>s.SubTotal).ToString("N2")},new(){ "ضريبة مبيعات محصّلة (مخرجات)",stax.ToString("N2")},new(){ "إجمالي المشتريات",purch.Sum(s=>s.SubTotal).ToString("N2")},new(){ "ضريبة المشتريات المسجّلة",ptax.ToString("N2")},new(){ "صافي ضريبة المبيعات قبل تسوية ضريبة المدخلات القابلة للخصم", (stax-ptax).ToString("N2")}}; return R("ملخص الضريبة",new(){ "البند","المبلغ"},rows); }
            case "sales-tax": { var d=await GetSalesTaxReportAsync(from,to); var rows=d.Rows.Select(r=>new List<string>{r.TaxRate.ToString("0.#")+"%", r.TaxDescription, r.InvoiceCount.ToString(), r.GrossAmount.ToString("N2"), r.TaxAmount.ToString("N2")}).ToList(); return R("تقرير ضريبة المبيعات",new(){ "نسبة الضريبة","وصف الضريبة","عدد الفواتير","إجمالي المبيعات","مبلغ الضريبة"},rows,new(){ "","الإجمالي",d.TotalInvoices.ToString(),d.TotalGross.ToString("N2"),d.TotalTax.ToString("N2")},$"مدين {d.TotalDebit:N2} = دائن {d.TotalCredit:N2}"+(d.IsBalanced?" — متوازن":" — غير متوازن!"),true); }
            case "sales-tax-register": { var d=await GetSalesTaxRegisterAsync(from,to); var rows=d.Rows.Select(r=>new List<string>{r.InvoiceDate.ToString("yyyy-MM-dd"), r.InvoiceNumber, r.CustomerName, r.ItemTotal.ToString("N2"), r.DiscountTotal.ToString("N2"), r.NetTaxable.ToString("N2"), r.TaxTotal.ToString("N2"), r.TotalInclTax.ToString("N2"), r.TaxRates}).ToList(); return R("سجل ضريبة المبيعات",new(){ "التاريخ","رقم الفاتورة","العميل","إجمالي الأصناف","الخصم","الصافي","الضريبة","الإجمالي","النسب"},rows,new(){ "","الإجمالي",d.Rows.Count.ToString(),d.TotalItemTotal.ToString("N2"),d.TotalDiscount.ToString("N2"),d.TotalNetTaxable.ToString("N2"),d.TotalTax.ToString("N2"),d.TotalInclTax.ToString("N2"),""},null,true); }
            case "purchase-tax": { var d=await GetPurchaseTaxReportAsync(from,to); var rows=d.Rows.Select(r=>new List<string>{r.TaxRate.ToString("0.#")+"%", r.TaxDescription, r.InvoiceCount.ToString(), r.GrossAmount.ToString("N2"), r.TaxAmount.ToString("N2")}).ToList(); return R("تقرير ضريبة المشتريات",new(){ "نسبة الضريبة","وصف الضريبة","عدد الفواتير","إجمالي المشتريات","مبلغ الضريبة"},rows,new(){ "","الإجمالي",d.TotalInvoices.ToString(),d.TotalGross.ToString("N2"),d.TotalTax.ToString("N2")},$"مدين {d.TotalDebit:N2} = دائن {d.TotalCredit:N2}"+(d.IsBalanced?" — متوازن":" — غير متوازن!"),true); }
            case "purchase-tax-register": { var d=await GetPurchaseTaxRegisterAsync(from,to); var rows=d.Rows.Select(r=>new List<string>{r.InvoiceDate.ToString("yyyy-MM-dd"), r.InvoiceNumber, r.SupplierName, r.ItemTotal.ToString("N2"), r.DiscountTotal.ToString("N2"), r.NetTaxable.ToString("N2"), r.TaxTotal.ToString("N2"), r.TotalInclTax.ToString("N2"), r.TaxRates}).ToList(); return R("سجل ضريبة المشتريات",new(){ "التاريخ","رقم الفاتورة","المورد","إجمالي الأصناف","الخصم","الصافي","الضريبة","الإجمالي","النسب"},rows,new(){ "","الإجمالي",d.Rows.Count.ToString(),d.TotalItemTotal.ToString("N2"),d.TotalDiscount.ToString("N2"),d.TotalNetTaxable.ToString("N2"),d.TotalTax.ToString("N2"),d.TotalInclTax.ToString("N2"),""},null,true); }
            case "tax-reconciliation": { var d=await GetTaxReconciliationAsync(from,to); var rows=new List<List<string>>{ new(){ "إجمالي المبيعات الخاضعة", d.TotalSalesGross.ToString("N2")}, new(){ "ضريبة المبيعات (مخرجات)", d.SalesTaxOutput.ToString("N2")}, new(){ "إجمالي المشتريات الخاضعة", d.TotalPurchasesGross.ToString("N2")}, new(){ "ضريبة المشتريات المسجّلة", d.RecordedPurchaseTax.ToString("N2")}, new(){ "صافي ضريبة المبيعات قبل تسوية ضريبة المدخلات القابلة للخصم", d.NetSalesTaxBeforeVat.ToString("N2")}, new(){ "الحالة", "غير نهائية — ضريبة المدخلات القابلة للخصم غير معروفة"}}; return R("تسوية الضريبة الموحّدة",new(){ "البند","القيمة"},rows,null,$"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}",true); }
            case "financial-ratios": { var d=await GetFinancialRatiosAsync(from,to); var rows=new List<List<string>>(); foreach(var c in d.Categories) foreach(var it in c.Items) rows.Add(new List<string>{ it.Key, (it.CurrentValue?.ToString("0.##")??"—"), (it.PreviousValue?.ToString("0.##")??"—"), it.IsPositiveMove?"↑":"↓" }); return R("المؤشرات المالية",new(){ "المؤشر","الفترة الحالية","الفترة السابقة","الاتجاه"},rows,null,$"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}",true); }
            case "sales-by-customer": { var q=await _context.Set<ERPSystem.Domain.Entities.SalesInvoice>().Where(s=>!s.IsDeleted && s.InvoiceDate>=from.Date && s.InvoiceDate<to.Date.AddDays(1)).Include(s=>s.Customer).ToListAsync(); var g=q.GroupBy(s=>s.Customer?.NameAr??s.CustomerId.ToString()).Select(x=>new{Name=x.Key,Tot=x.Sum(s=>s.TotalAmount),Cnt=x.Count()}).OrderByDescending(x=>x.Tot).ToList(); var rows=g.Select(x=>new List<string>{x.Name,x.Cnt.ToString(),x.Tot.ToString("N2")}).ToList(); return R("المبيعات حسب العميل",new(){ "العميل","عدد","الإجمالي"},rows); }
            // المرحلة D: تقارير سندات القبض/الصرف وقيود المستندات الأربعة
            // (المحرّك في ReportServiceDocumentReports.cs — مصدر واحد: سطور القيود، وإجماليات على مستوى البند)
            case "receipt-vouchers":
            case "receipt-voucher-entries":
            case "payment-vouchers":
            case "payment-voucher-entries":
            case "sales-invoice-entries":
            case "sales-return-entries":
            case "purchase-invoice-entries":
            case "purchase-return-entries":
                return await GetDocumentReportAsync(key, pars, from, to);
            default: return await GetReportAsyncB(key,pars,from,to,asOf,R);
        }
    }
}
