# ERP-P0-2F — إصلاح محدود لشروط تقرير أعمار ذمم العملاء

| البند | القيمة |
|---|---|
| النوع | **إصلاح إنتاجي محدود** (شروط استعلام فقط) — بلا Migration/مخطط · بلا تعديل قيود أو أرصدة · بلا تخصيص مقبوضات · بلا تغيير لدورة الشيك |
| التاريخ (UTC) | 2026-10-23 |
| قاعدة الاختبار | PostgreSQL معزولة `127.0.0.1:15432` (ليست إنتاجًا) · كل البيانات عبر الخدمات الرسمية |
| الإصلاح | ✅ **مكتمل** — اختباران فاشلان قبله، **5/5 ناجح** بعده |

---

## 1) وصف الخلل قبل الإصلاح والدليل عليه

**فرع العملاء في `AgingReportQueries.GetAgingAsync`** كان:
```csharp
.Where(s => !s.IsDeleted && s.Status != DocumentStatus.Cancelled)   // بلا Posted وبلا asOf
var bal = s.TotalAmount - s.PaidAmount;                             // (عيب منفصل — لم يُصلح هنا)
```
**فرع الموردين** كان (ولا زال) صحيحًا: `Status == Posted && InvoiceDate <= asOfDate` + متبقٍّ محسوب.

**الدليل (اختبار قبلي — فشل مؤكَّد):**
| الاختبار | قبل الإصلاح | التفسير |
|---|---|---|
| `AR_aging_respects_asOf_cutoff` | ❌ Expected `0` · Actual `100.00` | لا يطبّق `asOf`: فاتورة اليوم تظهر في تقرير «أمس» |
| `AR_aging_excludes_non_posted_invoices` | ❌ Expected `0` · Actual `100.00` | لا يشترط `Posted`: فاتورة «مسودة» تُحتسب ذممًا |
| `Running_reports_does_not_change_ledger` | ✅ | التقارير قراءة فقط |
| `AP_aging_cutoff_still_works` | ✅ | فرع الموردين سليم أصلًا |
**الأمر:** `dotnet test … --filter P02FAgingQueryFixTests` → ❌ `Failed: 2, Passed: 2, Total: 4` (3 ث).

**توسّع نطاق العيب (من P0-2C):** `ReportServiceReportsB.GetAging` يستخدم نفس المعادلة لعملاء و**يخلو من شرط `Posted`** في فرع العملاء ⇒ شملته نفس الجرعة (بلا تغيير لفرع الموردين).

---

## 2) الملفات المعدّلة وبصماتها قبل/بعد

| الملف | SHA256 قبل | SHA256 بعد | التغيير |
|---|---|---|---|
| `src\Application\Services\AgingReportQueries.cs` | `9D36FADEAA0A0C7F` | **`7F2C93E4B550335A`** | فرع العملاء: `Status == Posted && InvoiceDate <= asOfDate` (3 أسطر + تعليق) |
| `src\Application\Services\ReportServiceReportsB.cs` | `3E9E9FD2DB852C05` | **`D5AFC6F46729B6D5`** | فرع `ar-aging`: `Status == Posted` |
| `tests\ERPSystem.Tests\P02FAgingQueryFixTests.cs` | — | **جديد** | 4 اختبارات (P0-2F) |

**لم تُعدّل** أي خدمة أخرى: `BankCheckService` · `ReceiptVoucherService` · `SalesInvoiceService` · `SalesReturnService` (بصمات R3/P0-2E كما هي).

**نسخة التراجع:** `D:\erpfix-pu01\_backup-before-p02ffix\` = نص **الأصلي الحرفي** للسطرين المعدّلين + نسختا الملفين بعد الإصلاح (`.postfix`) + بصماتهما ⇒ **التراجع ممكن بإعادة السطرين الأصليين** (موثَّقان في `REVERT-LOG.txt`).

---

## 3) الإصلاح المطبَّق (أقل تعديل ممكن)
```
AgingReportQueries (فرع العملاء):  Status == DocumentStatus.Posted  AND  InvoiceDate <= asOfDate
ReportServiceReportsB (ar-aging) :  Status == DocumentStatus.Posted
```
- **لم يُغيَّر** فرع الموردين (سليم أصلًا) · **لم تُغيَّر** معادلة `Total − PaidAmount` (**عيب منفصل** يحتاج سياسة تخصيص المقبوضات = P0-2D) · **لم يُعدَّل** أي قيد أو رصيد أو دورة شيك.

---

## 4) الاختبارات ونتائجها (بعد الإصلاح)
**الأمر:** `dotnet test tests\ERPSystem.Tests\ERPSystem.Tests.csproj --filter "FullyQualifiedName~P02FAgingQueryFixTests|FullyQualifiedName~P02ReconciliationProbeTests"`
**النتيجة:** ✅ **`Failed: 0, Passed: 5, Skipped: 0, Total: 5`** (7 ث)

| # | الاختبار | النتيجة |
|---|---|---|
| 1 | `AR_aging_respects_asOf_cutoff` — فاتورة 100 بتاريخ **اليوم** | ✅ تظهر عند `asOf = اليوم` (**حد شامل**) وتختفي عند `asOf = أمس` |
| 2 | `AR_aging_excludes_non_posted_invoices` — تحويل مستند الاختبار إلى «مسودة» | ✅ تختفي من الذمم (ثم أُعيدت الحالة إلى `Posted`) |
| 3 | `Running_reports_does_not_change_ledger` | ✅ `COUNT`/`SUM(مدين)`/`SUM(دائن)` **متطابقة قبل وبعد** تشغيل التقريرين |
| 4 | `AP_aging_cutoff_still_works` | ✅ فرع الموردين كما كان (يظهر في اليوم، غائب في أمس) |
| 5 | `P02ReconciliationProbeTests` (P0-2A) | ✅ ناجح (لم يتراجع) |

**حالات الحدود المُختبرة:** `InvoiceDate == asOf` ⇒ **مشمولة** (`<=`) · `InvoiceDate > asOf` ⇒ مستبعدة · الحالة `Posted` ⇒ مشمولة · `Draft` ⇒ مستبعدة. **دقة الحقل `Date`** (لا وقت) ⇒ لا حساسية زمنية إضافية.

**لم يُشغَّل**: الحزمة الكاملة (التزامًا بالقيد) · لم تُعَد اختبارات ناجحة سابقًا بلا سبب.

---

## 5) إثبات عدم تغيير القيود أو الأرصدة
- اختبار (3) يقارن `COUNT` و`SUM(مدين)` و`SUM(دائن)` لكل سطور القيود **قبل/بعد** تشغيل تقريري العملاء والموردين ⇒ **متطابقة**.
- الإصلاح **لا يكتب** أي شيء (استعلامات قراءة فقط) ⇒ لا أثر على الأستاذ ولا على `Customer.CurrentBalance` ولا على دورة الشيك (`1102`).

---

## 6) القيود والمسائل غير المحلولة
| # | البند | الحالة |
|---|---|---|
| 1 | **معادلة `Total − PaidAmount`** — الأعمار ما زالت تجهل سندات القبض والمردودات والشيكات (فرق 50.00 في سيناريو P0-2E و64.80 في M1A) | **لم تُصلح** — تحتاج **قرار تخصيص المقبوضات** (P0-2D) |
| 2 | `ReportServiceReportsB` يستخدم **فترة `from..to`** لا `asOf` | **سجل** المعنى: تقرير «فترة» لا تقرير «حتى تاريخ» — تغييره قرار نطاق |
| 3 | **الأرصدة التاريخية** | لم تُمَس ولم تُسوَّ |
| 4 | تخصيص المقبوضات / `CustomerPaymentAllocation` / Migration | **لم تُنفَّذ** (ممنوعة في هذه المهمة) |
| 5 | **ملاحظة أمانة:** لم أُنشئ نسخة احتياطية *قبل* التعديل مباشرة؛ النسخة أُنشئت بعده وتحتوي **النص الأصلي الحرفي** للسطرين + الملفين بعد الإصلاح ⇒ التراجع ممكن، والقيد موثَّق هنا | موثَّق |

---

## 7) تأكيد الالتزام بالقيود
✅ قاعدة اختبار معزولة (والاتصال مُتحقَّق عبر `AccountingFixture.Available` قبل أي كتابة) · ✅ لا إنتاج/VPS/نشر · ✅ لا Migration/تغيير مخطط · ✅ لا تعديل أرصدة أو قيود تاريخية · ✅ لا Git · ✅ لا PU-05 ولا سيناريو محاسبي شامل · ✅ **لم تُغيَّر دورة الشيك** (`BankCheckService` بلا تعديل) · ✅ **لم تُضف قيمة الشيكات تحت التحصيل إلى الذمم** · ✅ **لم يُنفَّذ تخصيص مقبوضات** ولا أُنشئ `CustomerPaymentAllocation` · ✅ لم تُشغَّل الحزمة الكاملة · ✅ كل التقارير والنسخ السابقة محفوظة.

**معيار الإغلاق:** ✅ أُصلحت **شروط الاستعلام فقط** مع اختبارات محددة ناجحة (5/5) وتقرير موثَّق.
**توقف العمل هنا** — لا تبدأ مرحلة تخصيص الدفعات (P0-2D) قبل اعتمادك.
