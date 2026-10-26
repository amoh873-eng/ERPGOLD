# ERP-CORE-ACCOUNTING-P7-REPAIR-REPORT
**المرحلة:** P7 — إصلاح G1 و G2
**التاريخ:** 2026-10-26
**النطاق:** عزل الأسباب الجذرية → إصلاحات حدّها الأدنى عبر الكود → أدلة قبل/بعد → اختبارات انحدار → بناء كامل → متصفح حقيقي (حيثما أمكن) → تحقق نهائي من الامتثال.

---

## 1) الملخص التنفيذي

| البند | G1 | G2 |
|---|---|---|
| العرض | شاشة حركات المخزون تعرض نوع «رصيد افتتاحي» بدون ترحيل أي قيد على حساب المخزون 1300 | ارتداد شيك فاتورة يعيد الدين على 1200 ورصيد العميل لكن يترك الفاتورة «مدفوعة» وتختفي من تقرير أعمار الذمم |
| القياس قبل الإصلاح | قيمة المخزون **1000.00** مقابل أثر 1300 = **0.00** ⇒ فرق **1000.00** | `invoice.Paid=40` · `1200=40` · `customer=40` · **أعمار الذمم = 0.00** |
| الجذر | `StockMovementService` مسار **كميّ/تكلفة فقط** بلا أي مرجع لـ Journal/Account (بالتصميم) | `BankCheckService.BounceAsync` يعكس القيد ورصيد العميل لكنه **لا يعيد فتح الفاتورة** (PaidAmount يبقى = الإجمالي) |
| الإصلاح | **واجهة/سياسة**: إزالة الخيار من شاشة الحركات + تنبيه ورابط للمسار الرسمي + ملاحظة تفسيرية في سطر 1300 — **بلا تغيير سياسة ترحيل** و بلا قيد جديد | **كود**: إعادة فتح الفاتورة المرتبطة داخل نفس معاملة الارتداد (`PaidAmount -= الشيك`) — خافض لا مضاعِف |
| القياس بعد الإصلاح | الدفعة الرسمية: القيمة **1000.00** = أثر 1300 **1000.00** ⇒ فرق **0.00** (المسار الرسمي سليم) | `invoice.Paid=0` · `1200=40` · `customer=40` · **أعمار الذمم = 40.00** (مطابق لـ1200) |
| الازدواجية | إعادة ترحيل الدفعة مرفوضة (فشل صاخب) | ارتداد ثانٍ لنفس الشيك مرفوض بلا أي أثر إضافي |
| النتائج | بناء Web/Api/Tests: **0 أخطاء** · فحوص P7: **8/8 ناجحة** · المجموعة الكاملة: **79/79 ناجحة** · متصفح حقيقي (Chrome): **10/10 PASSED** |

> قرار المالك/المدير المالي غير مطلوب هنا: G1 أُصلح على مستوى **واجهة المسار** لا سياسة الترحيل، و G2 طبَّق قاعدة معتمدة سلفًا («لا يُخصَّص شيك مرتجع — Bounced») على مستوى **حالة المستند** داخل نفس معاملة الارتداد.

---

## 2) G2 — ارتداد شيك الفاتورة: الدين يعود لكن المستند يبقى «مدفوعًا»

### 2.1 إعادة الإنتاج (قياس فعلي قبل/بعد)

السيناريو المستنسخ: فاتورة بيع 2 × 20 = **40** مسدَّدة بشيك (`PaymentMethod=Check`) ثم ارتداد الشيك.

| المتري | قبل (P7_PHASE=before) | بعد (P7_PHASE=after) |
|---|---|---|
| `invoice.TotalAmount` | 40.00 | 40.00 |
| `invoice.PaidAmount` | **40.00** | **0.00** |
| `Customer.CurrentBalance` | 40.00 | 40.00 |
| `gl1200` (الأستاذ) | 40.00 | 40.00 |
| `check.Status` | Bounced | Bounced |
| أعمار الذمم — مستوى الطرف (`aging_party`) | **0.00** | **40.00** |
| مستقطع الفاتورة (`aging_invoice_residual`) | **0.00** | **40.00** |
| ارتداد ثانٍ (`secondBounce`) | InvalidOperationException | InvalidOperationException |
| `paidAfterSecond` / `customerAfterSecond` | 40.00 / 40.00 | 0.00 / 40.00 (لم يتغيرا — لا ازدواج) |

المصدر: `_p7_evidence.txt` (سطور `[before]` مقابل `[after]`).

### 2.2 السبب الجذري

- عند **تسجيل** فاتورة بشيك تُنشأ بـ `PaidAmount = TotalAmount` (الشيك يُعدّ تسويةً وقت التسجيل).
- عند **ارتداد** الشيك يعكس `BankCheckService.BounceAsync` القيد (`1200 D / 1102 C`) ويعيد `Customer.CurrentBalance` — لكنه **لا يلمس `PaidAmount`** ⇒ تبقى الفاتورة «مدفوعة».
- تقرير **أعمار الذمم** مبني على متبقي المستندات (`TotalAmount − PaidAmount`) ⇒ تختفي الذمة من تقرير الأعمار بينما 1200 يعكس الدين ⇒ **ذمة غير مرئية** للمحاسب مع بقاء الرقابي سليمًا (فجوة تشغيلية لا محاسبية).

### 2.3 الإصلاح (ملف واحد: `BankCheckService.cs`)

في فرع **الشيك المستلم** داخل `BounceAsync` وضمن **نفس معاملة** انعكاس القيد:

```csharp
// داخل BounceAsync (received && check.RelatedInvoiceId != null && check.Amount > 0)
var linkedInvoice = await _context.Set<SalesInvoice>()
    .FirstOrDefaultAsync(i => i.Id == check.RelatedInvoiceId.Value && !i.IsDeleted);
if (linkedInvoice is not null)
{
    var reopened = Math.Min(check.Amount, linkedInvoice.PaidAmount);
    if (reopened > 0m)
    {
        linkedInvoice.PaidAmount = MoneyRounding.Round(linkedInvoice.PaidAmount - reopened);
        linkedInvoice.UpdatedAt = DateTime.UtcNow;
        check.Notes += $"ارتداد: أُعيد فتح الفاتورة {linkedInvoice.InvoiceNumber} بمبلغ {reopened:N2}";
    }
}
```

المنطق:
- **القاعدة المعتمدة**: «لا يُخصَّص شيك مرتجع (Bounced)» (P0-2K §239 / P0-2L §D3) ⇒ بعد الارتداد لم يعد الشيك تسوية، فيجب أن تعود حالة الدفع على مستوى المستند.
- **لا مساس بالقيود/الحسابات**: تراجع الارتداد (1200/العميل) يحدث كما كان سابقًا تمامًا.
- **خافض لا مضاعِف**: `Math.Min` يمنع السالب؛ الشيك المرتجع مستبعد أصلًا من تجميعات الشيكات في الأعمار؛ والارتداد الثاني مرفوض قبل أي تعديل ⇒ لا تأثير مزدوج.
- **فرع الموردين** (شيك صادر مرتد): لا يوجد مستند مرتبط يُعاد فتحه في هذا المسار — لم يُلمس.

### 2.4 الاختبارات (اختباران)

- `P7ChequeBounceStateTests.Bounced_cheque_reopens_the_invoice_brings_aging_to_life_once_and_never_twice`
  — (1) قبل الارتداد: `Paid=40` و`residual=0`؛ (2) بعد الارتداد: `Paid=0` · `customer=40` · `gl1200=40` · `residual=40` · `aging_party=40` · `aging_party==gl1200`؛ (4) ارتداد ثانٍ مرفوض بلا أي قيد/خفض جديد (قِياس عدد القيود قبل/بعد).
- `P7ChequeBounceStateTests.Collected_cheque_keeps_the_invoice_paid_and_aging_clear`
  — الشيك **المحصَّل** تسوية صحيحة ⇒ تبقى الفاتورة `Paid=40` و`residual=0`، وقياس تفاضلي (رصيد العميل و1200 دون تغيير) — يثبت أن الإصلاح لا يمسّ غير المرتجع.

---
## 3) G1 — «رصيد افتتاحي» من شاشة حركات المخزون بلا قيد على 1300

### 3.1 إعادة الإنتاج (قياس فعلي)

| المتري | قبل الإصلاح (حركة افتتاحية من شاشة الحركات) | بعد الإصلاح (نفس السلوك للحركات العادية) |
|---|---|---|
| كمية/تكلفة عبر `StockMovementService.CreateAsync` (نوع OpeningBalance) | 100 × 10 | 100 × 10 |
| قيمة المخزون (`stock × cost`) | 1000.00 | 1000.00 |
| أثر حساب 1300 في الأستاذ | **0.00** | **0.00** |
| الفجوة | **1000.00** | **1000.00** |

> الفجوة المذكورة مرتبطة بمسار شاشة الحركات فقط: «حركة افتتاحية» من هذا المسار لا تُرحّل قيدًا (بالتصميم). أما **المسار الرسمي** — دفعة الأرصدة الافتتاحية — فيُثبت تطابقه بعد الإصلاح:

| الدفعة الرسمية (`OpeningBalanceService`: سطر 1300 دائن 1000 + سطر 1100 · مع `ItemCode` وكمية 100 × 10) | القيمة |
|---|---|
| الكمية | 100 |
| التكلفة | 10.00 |
| قيمة المخزون (`stock × cost`) | 1000.00 |
| أثر 1300 (Δ قبل/بعد الترحيل) | **1000.00** |
| **الفجوة** | **0.00** |
| إعادة ترحيل نفس الدفعة | **مرفوضة** (لا قيد ثانٍ ولا حركة ثانية) |

المصدر: `_p7_evidence.txt` — سطور `[before] G1` و `[after] G1-official`.

> فجوة سيناريو مصفوفة P6 التاريخية (قيمة مخزون 892 مقابل 1300 = −108، فرق 1000) هي نفس الظاهرة: افتتاحي بلا قيد بقيمة 1000 + حركات مستندية بـ −108 على 1300 ⇒ الفرق مولَّد من الحركة الافتتاحية غير المرحَّلة بالضبط.

### 3.2 السبب الجذري

- `StockMovementService` (المسار الذي ينفّذه `MovementFormDialog`) **كميّ/تكلفة فقط بالتوريث**: لا يوجد في الملف أي مرجع إلى `JournalEntry`/`Account`/قيد — لا استدعاء محاسبي في الملف كله (مؤكَّد بمسح المراحل السابقة).
- عرض نوع «رصيد افتتاحي» في شاشة الحركات كان يوهم بأن المَدخل **مثبت ماليًا** (قيمة مخزون في التقارير) بينما لا يوجد أي قيد على 1300 ⇒ ميزانية ناقصة وتفسير خاطئ لصف 1300 في المطابقة.

### 3.3 الإصلاح (واجهة/سياسة — لا تغيير في الترحيل)

القرار: **إزالة المسار الخاطئ من سطح الإدخال وتوجيه المستخدم إلى المسار الرسمي** بلا اختراع أي ترحيل تلقائي (القرارات المالية F-A..F-E ما زالت معلّقة لدى المالك/المدير المالي).

1. `MovementFormDialog.razor` — حذف `<MudSelectItem Value="3">` (نوع «رصيد افتتاحي») من قائمة نوع الحركة + **تنبيه إرشادي داخل الحوار** يوجه لـ `/accounting/opening-balances`.
2. `Movements.razor` — **تنبيه إرشادي دائم** أعلى الصفحة بنفس الرابط.
3. `SharedResources.resx` / `SharedResources.en.resx` — مفتاحان معرّبان باللغتين (`OpeningBalanceOfficialPathHint`, `OpeningBalanceOfficialPathReportNote`) — لا نص صلب في الواجهة.
4. `SubLedgerReconciliationService.cs` — **ملاحظة تفسيرية** على صف 1300: «حركات رصيد افتتاحي من شاشة حركات المخزون مسار كمّي/تكلفة لا يُنشئ قيدًا على 1300 ⇒ أي فرق هنا قد يكون مصدره تلك الحركات» — تغيير توثيقي لا يغيّر المنطق أو السياسة.

> تُرك `MovementType.OpeningBalance` في التعداد ودعم أي تكامل قديم قائمًا؛ الإزالة على مستوى **سطح الإدخال** فقط (بلا حذف/تغيير API).

### 3.4 الاختبارات (3 اختبارات)

- `P7OpeningMovementPathTests.Movements_screen_no_longer_offers_opening_balance_and_guides_to_the_official_path`
  — `MovementFormDialog.razor` بلا `Value="3"` + يحتوي المفتاح والرابط؛ `Movements.razor` يحتوي المفتاح والرابط؛ المفتاحان موجودان في اللغتين.
- `P7OpeningMovementPathTests.Stock_movement_service_remains_quantity_only_by_design`
  — **حارس فشل صاخب**: أي إضافة قيد جزئي إلى `StockMovementService.cs`/`Partial.cs` تكسر هذا الفحص (تمنع الازدواج مع قيود المستندات).
- `P7OpeningMovementPathTests.Official_opening_balance_batch_entry_value_equals_1300_matched`
  — الدفعة الرسمية: كمية 100 × 10 ⇒ قيمة 1000 = Δ1300 (فرق 0) + منع إعادة الترحيل.

---

## 4) فحوص الانحدار الجديدة (إجمالي 8)

| الاختبار | النوع | الغرض |
|---|---|---|
| `P7ReproductionProbeTests.G1_opening_movement_from_movements_screen_vs_account_1300` | LivePg/قياس | يسجّل قبل/بعد: قيمة المخزون مقابل أثر 1300 |
| `P7ReproductionProbeTests.G1_official_open_batch_value_vs_1300_matched` | LivePg/قياس | يسجّل قبل/بعد: الدفعة الرسمية بلا فجوة |
| `P7ReproductionProbeTests.G2_cheque_bounce_vs_invoice_state_and_aging` | LivePg/قياس | يسجّل قبل/بعد: Paid/1200/العميل/الأعمار + منع التكرار |
| `P7ChequeBounceStateTests.Bounced_cheque_reopens_the_invoice_brings_aging_to_life_once_and_never_twice` | انحدار | G2: إعادة فتح الفاتورة · الأعمار = 1200 · لا مضاعفة |
| `P7ChequeBounceStateTests.Collected_cheque_keeps_the_invoice_paid_and_aging_clear` | انحدار | G2: التحصيل لا يمسّ المستند (قياس تفاضلي) |
| `P7OpeningMovementPathTests.Movements_screen_no_longer_offers_opening_balance_and_guides_to_the_official_path` | ثابت (قراءة ملفات) | G1: سطح الإدخال + التوجيه + التعريب |
| `P7OpeningMovementPathTests.Stock_movement_service_remains_quantity_only_by_design` | ثابت (حارس) | G1: لا قيد جزئي في الخدمة |
| `P7OpeningMovementPathTests.Official_opening_balance_batch_entry_value_equals_1300_matched` | LivePg | G1: المسار الرسمي متطابق + لا إعادة ترحيل |

**نتيجة `FullyQualifiedName~P7`: ناجحة — 8/8** (`Passed: 8, Failed: 0, Skipped: 0`, ~7 ثوانٍ) — `_p7/run_p7all.txt`.

> الاختبارات القياسية (Probe) مسوّقة `[before]`/`[after]` عبر متغير البيئة `P7_PHASE` وتسجّل في `_p7_evidence.txt`: أداة دليل، وليست بوابة دائمة.

## 5) بناء المشاريع (0 أخطاء)

| المشروع | نتيجة p7all (2026-10-26 02:27) | نتيجة full (02:48–02:49) |
|---|---|---|
| `src/Web/ERPSystem.Web.csproj` | Build succeeded · 0 Errors | Build succeeded · 0 Errors |
| `src/Api/ERPSystem.Api.csproj` | Build succeeded · 0 Errors | Build succeeded · 0 Errors |
| `tests/ERPSystem.Tests/ERPSystem.Tests.csproj` | Build succeeded · 0 Errors | Build succeeded · 0 Errors |

الملفات: `_p7/build_*.txt` · `_p7/run_p7all_status.txt` · `_p7/run_full_status.txt`.

## 6) المجموعة الكاملة من فحوص الاختبار

| الفحص | النتيجة |
|---|---|
| مزوّد الاختبارات | `dotnet test tests/ERPSystem.Tests (filter: ALL)` على بيئة Postgres الحية المعزولة |
| **النتيجة** | **Passed! — Failed: 0 · Passed: 79 · Skipped: 0 · Total: 79 · Duration: 2m 15s** |
| السجل | `_p7/run_full.txt` · `_p7/run_full_status.txt` (02:46→02:51، وبناء المشاريع الثلاثة قبله 0 أخطاء) |

> لا يوجد أي اختبار مفشل أو مهمل؛ كل فحوص الانحدار سابقة (P0–P6) والجديدة (P7) تعمل معًا على نفس القاعدة المعزولة.

## 7) اختبار المتصفح الحقيقي (Chrome حقيقي — الجولة الثالثة المعتمدة)

### 7.1 المنهج

- متصفح **Chrome 135.0.7049.115** فعلي (headless) يُقاد عبر **CDP** (`_p7/ui_cdp.mjs`، بلا أي حزمة إضافية).
- التطبيق الحقيقي `src/Web` يعمل على **قاعدة معزولة `erp_p7_ui`** (أُنشئت للاختبار ثم حُذفت) — `http://127.0.0.1:5199`.
- تهيئة أولية عبر `/setup` (توكن الاختبار) ثم **تسجيل دخول فعلي من صفحة `/login`** ببيانات مُنشأة في القاعدة المعزولة.
- الأدلة: `_p7/ui_result.json` (JSON كامل) · `_p7/ui_node.log` · `_p7/ui_status.txt` · `_p7/shots/*.png` (6 لقطات).

### 7.2 النتيجة الإجمالية: **`PASSED` — 10/10** (`failed: []`)

| # | الفحص | النتيجة | الدليل المختصر |
|---|---|---|---|
| 1 | صفحة التهيئة متاحة/منجزة | ✅ | `setup_page.state = FORM` ثم `setup_submit = SUBMITTED` |
| 2 | نموذج الدخول يُعرض | ✅ | `login_page.state = LOGIN_FORM` |
| 3 | مصادقة فعلية بعد الدخول | ✅ | `after_login.state = / \| نظام النبالي المحاسبي ERP …` (خرج من `/login`) |
| 4 | `/inventory/movements` يتحمل | ✅ | `path=/inventory/movements` · `textLen=509` |
| 5 | رابط المسار الرسمي ظاهر في الصفحة | ✅ | `hasOfficialPathLink=true` (تنبيه G1 أعلى الشاشة) |
| 6 | حوار «إضافة حركة» يُفتح فعليًا | ✅ | `dialog_open_click = CLICKED_إضافة حركة` ثم `dialog:true` |
| 7 | تنبيه المسار الرسمي داخل الحوار | ✅ | `hasHintLink=true` + نص: «هذه الشاشة تسجّل حركة كميّة/تكلفة فقط ولا تُرحّل أي قيد محاسبي؛ لإثبات رصيد افتتاحي مالي (قيد على 1300 + حركة المخزون معًا) استخدم مسار الأرصدة الافتتاحية الرسمي» |
| 8 | **لا يوجد خيار «رصيد افتتاحي»** في أنواع الحركة | ✅ | خيارات القائمة المقروءة من DOM: **4 فقط** = `وارد مشتريات · صادر مبيعات · تسوية إضافة · تسوية خصم` |
| 9 | `/accounting/opening-balances` يتحمل بلا خطأ | ✅ | `textLen=334` · `hasUnhandledError=false` |
| 10 | `/accounting/reconciliation` يتحمل بلا خطأ | ✅ | `textLen=1527` · `hasUnhandledError=false` |

### 7.3 ما لم يُختبر في المتصفح (بصراحة)

- **سير عمل التحصيل/الارتداد من الواجهة:** لم يُنفَّذ في المتصفح (يحتاج إنشاء فاتورة بشيك من الشاشات ثم الارتداد) — تم إثباته على مستوى الخدمة/قاعدة البيانات في فحوص P7 (8/8) وأدلة `_p7_evidence.txt`. الواجهة ذاتها لم تُثبت سلوك الارتداد بصريًّا.
- **`/health/detailed` يعيد 404** (المسار الفعلي للصحة مختلف في هذا الإصدار) — لذلك اعتمد مشغّل الإقلاع على استجابة المتصفح الفعلي بدل نقطة الصحة؛ لا أثر على أي فحص وظيفي.
- **جولة أولى (قبل التحسين):** فشل نقطتان فقط (فتح الحوار بسبب اختيار خاطئ للزر) — أُصلح مُشغّل الاختبار (مطابقة نصية للزر) وأُعيدت الجولة بالكامل بنجاح 10/10. لا عيب في المنتج نفسه.

### 7.4 اللقطات (بكسل حقيقي)

`_p7/shots/after_login.png` (144,622B) · `movements.png` (179,660B) · `movement_dialog.png` (163,315B) · `movement_type_options.png` (165,569B) · `opening_balances.png` (133,657B) · `reconciliation.png` (210,879B).

---
## 8) التغييرات على الملفات ومجاميع ما قبل/بعد (SHA-256)

| الملف | قبل (PRE_MANIFEST) | بعد (after_hashes) | الهدف |
|---|---|---|---|
| `src/Application/Services/BankCheckService.cs` | `6790D61B 3903BC4E BA2A3A45 09…` | `50C996BB D86825E4 7A456981 AC…` | G2: إعادة فتح الفاتورة عند ارتداد الشيك المستلم |
| `src/Web/.../MovementFormDialog.razor` | `817B94EE 3CD55942 AA79BB43 2F…` | `06E1231C 2533E9EE AD015DF2 4C…` | G1: حذف خيار «رصيد افتتاحي» + تنبيه إرشادي برابط |
| `src/Web/.../Movements.razor` | `267383FE 5CD05ABC C89DF5CC 50…` | `F4C317F7 B2AA4C19 0127BABD 42…` | G1: تنبيه إرشادي أعلى الصفحة |
| `src/Application/Services/SubLedgerReconciliationService.cs` | `DFB15F54 2C36602E 8D6D0A31 53…` | `11A2CDC8 C0E06D9B CF8E4EA7 D3…` | G1: ملاحظة تفسيرية على صف 1300 |
| `src/Web/Resources/SharedResources.resx` | `40A8CC9A 27174777 87A75D59 5A…` | `B7227F07 CDC12737 C5A458AB 3F…` | مفتاحا `OpeningBalanceOfficialPathHint` / `...ReportNote` |
| `src/Web/Resources/SharedResources.en.resx` | `D0732DEC 4BBFA368 C2C58461 A2…` | `B3DC4993 336FD7A3 57BB363B FB…` | نفسه بالإنجليزية |
| `tests/.../P7ChequeBounceStateTests.cs` | — (جديد) | `7A7843BE B0FA0905 A0797B33 2F…` | انحدار G2 |
| `tests/.../P7OpeningMovementPathTests.cs` | — (جديد) | `040F07CA 80C9E4FB E19FDAA3 34…` | انحدار G1 |
| `tests/.../P7ReproductionProbeTests.cs` | — (جديد) | `7CC3233C 603DCF4B B7DA2D5B 77…` | مسبار قبل/بعد |

- نسخ احتياطية كاملة قبل التعديل: `D:\erpfix-pu01\_p7_backup\` + `PRE_MANIFEST.txt` (بالمجاميع الكاملة وأعداد الأسطر).
- المجاميع الكاملة: `_p7_backup/PRE_MANIFEST.txt` (قبل) و `_p7/after_hashes.txt` (بعد).

## 9) حالة الفجوات/القرارات (من مراجعة §8 في تقرير P6 — تحديث P7)

| الرمز | البند | الحالة بعد P7 |
|---|---|---|
| **G1** | حركة «رصيد افتتاحي» من شاشة حركات المخزون بلا قيد على 1300 | **عولج المسار — الخيار (أ) من قائمة P6**: منع نوع الحركة من شاشة الحركات وإلزام الدفعة الرسمية (تنبيه + رابط في الشاشة والحوار). **لم يُنفَّذ الخيار (ب)** (ترحيل تلقائي = سياسة مالية) — يبقى للبتّ |
| **G2** | ارتداد شيك فاتورة لا يُعيد فتح المستند | **عولج — الخيار (أ) من قائمة P6**: إعادة فتح الفاتورة عند الارتداد (إنقاص `PaidAmount` بمبلغ الشيك داخل نفس معاملة الارتداد) |
| **F-A** | مخزون افتتاحي سالب | **يبقى مفتوحًا** (قرار مالي) — الحالي: مرفوض برسالة صريحة (D2) |
| **F-B** | سياسة تقييم المخزون الكاملة لمطابقة 1300 | **يبقى مفتوحًا**؛ P7 وسّع التوثيق على صف 1300 بالمطابقة (مساره كمّي/تكلفة لا يُرحّل) — لم يُغيَّر أي شيء في الترحيل |
| **F-C** | منع القيد اليدوي غير المُسند على 1200/2200 | **حُسم بالقرار القائم D1** (مسموح + إفصاح) — لا تغيير؛ بوابات الانحدار قائمة |
| **F-D** | منع العكس في فترة مغلقة + منع إغلاق فترة فيها مستندات معلّقة | **يبقى مفتوحًا** (تشديد رقابي يحتاج اعتمادًا) — السلوك الحالي مُثبت ومُختبر |
| **F-E** | قفل السنة المالية كاملة | **يبقى مفتوحًا** (قرار نطاق) |

> كلا الإصلاحين طبّقا **الخيارات المعتمدة سلفًا ضمن قوائم P6** (لم يُخترع سلوك جديد): G1 = الخيار (أ) على مستوى المسار/الواجهة، G2 = الخيار (أ) على مستوى حالة المستند. لم يُمسّ أي قرار مالي عائلي (F-A … F-E) ولم تُرتكب أي سياسة جديدة (لا ترحيل تلقائي G1-(ب)، لا إنشاء ذمة بديلة G2-(ب)).

## 10) التحقق النهائي من الامتثال

| الفحص | النتيجة |
|---|---|
| قواعد بيانات الكتلة (`pg_database`, بلا قوالب) | **3 فقط، مُقاسة بعد الاختبار**: `ERPSystemDb` · `erp_p02_ui_1` · `postgres` — لا `erp_p7_ui` (حُذفت بالتنظيف) |
| `erp_p02_ui_1` غير ممسوسة | **21 هجرة** (`SELECT count(*) FROM "__EFMigrationsHistory"` = 21) — لا إضافة/تغيير |
| لا قواعد `erp_a5_*` / `erp_ob_mig_*` | غير موجودة |
| بيانات إنتاج | **لم تُلمس** — الاختبارات على `ERPSYSTEM_TEST_PG` (قاعدة `postgres`) والمتصفح على `erp_p7_ui` ثم حُذفت |
| عمليات مؤقتة | المنفذ **5199 حر** · لا Chrome بملف تعريف `_p7_chrome` متبقٍ (فحص سطور أوامر العمليات) |
| Git / النشر | P7 نفسه: تغييرات محلية + نسخ احتياطية فقط. الرفع إلى `amoh873-eng/ERPGOLD` (فرع `main`) تم **بعد** هذا التقرير وبنفس المحتوى المُجمَّع — لا `--force`، لا لمس VPS/إنتاج |

## 11) خطوات إعادة التحقق (playbook مختصر)

1. البناء: `dotnet build pkg-source/src/Web/ERPSystem.Web.csproj` ثم Api ثم Tests (0 أخطاء).
2. فحوص P7: `ERPSYSTEM_TEST_PG=… P7_PHASE=after dotnet test --filter FullyQualifiedName~P7` → 8/8 (يسجّل سطور `[after]` في `_p7_evidence.txt`).
3. المجموعة الكاملة: `dotnet test --filter ALL` → 79/79.
4. واجهة: تشغيل Web على قاعدة معزولة وتسجيل الدخول → `/inventory/movements` (بدون خيار «رصيد افتتاحي» + تنبيه برابط) → `/accounting/opening-balances` و `/accounting/reconciliation` → سير عمل التحصيل/الارتداد لشيك فاتورة إن وُجدت شاشة (انظر قسم المتصفح 7).
5. مقارنة مجاميع الملفات بعد ذلك ضد `_p7/after_hashes.txt` و`PRE_MANIFEST.txt`.

### 11.1 استنساخ بيئة الاختبار كما نُفّذت

```text
# 1) الاختبارات (قاعدة Postgres معزولة موجودة مسبقًا على المنفذ 15432)
set ERPSYSTEM_TEST_PG=Host=localhost;Port=15432;Database=postgres;Username=postgres;Password=<محلي>
dotnet test pkg-source\tests\ERPSystem.Tests\ERPSystem.Tests.csproj --filter FullyQualifiedName~P7   # 8/8
dotnet test pkg-source\tests\ERPSystem.Tests\ERPSystem.Tests.csproj --filter ALL                       # 79/79

# 2) المتصفح (قاعدة معزولة erp_p7_ui تُنشأ ثم تُحذف) — السكربتات في _p7/
powershell -File _p7\ui_all.ps1     # إقلاع Web على 127.0.0.1:5199 + Chrome CDP + تنظيف تلقائي
```

## 12) فهرس الأدلة وبصمة التقرير

### 12.1 أدلة الاختبار (محلية — `D:\erpfix-pu01\_p7\`)

| الدليل | الوصف |
|---|---|
| `_p7_evidence.txt` | قياسات G1/G2 قبل/بعد (موسومة `[before]`/`[after]`) |
| `_p7/run_p7all.txt` · `run_p7all_status.txt` | فحوص P7: 8/8 + بناء المشاريع الثلاثة (0 أخطاء) |
| `_p7/run_full.txt` · `run_full_status.txt` | المجموعة الكاملة: 79/79 (02:51:54، 2م15ث) |
| `_p7/ui_result.json` · `ui_node.log` · `ui_status.txt` | اختبار المتصفح: `PASSED` 10/10 + الخيارات الأربعة |
| `_p7/shots/*.png` (6 ملفات) | لقطات شاشة حقيقية (133–211 ك.ب) |
| `_p7/after_hashes.txt` · `_p7_backup/PRE_MANIFEST.txt` | مجاميع SHA-256 بعد/قبل التعديل + النسخ الاحتياطية |
| `_p7/{ui_boot,ui_cdp.mjs,ui_all,run3}.ps1` | سكربتات إعادة الإنتاج (إقلاع/متصفح/اختبارات) |

### 12.2 تعريف بصمة التقرير

- البصمة أدناه **محسوبة على محتوى هذا الملف باستثناء سطره الأخير** (لأن أي قيمة ذاتية تُدرج في السطر الأخير تتغيّر بإدراجها ⇒ استُثني السطر الأخير لتكون البصمة ثابتة وقابلة للتحقق).
- طريقة التحقق الحرفية (نهايات أسطر موحَّدة LF): احذف السطر الأخير، وحِّد `CRLF→LF`، ثم احسب SHA-256.
  ```powershell
  $p='ERP-CORE-ACCOUNTING-P7-REPAIR-REPORT.md'
  $t=([IO.File]::ReadAllText($p)) -replace "`r`n","`n"
  $t=$t.TrimEnd("`n"); $t=$t.Substring(0,$t.LastIndexOf("`n")+1)
  (([Security.Cryptography.SHA256]::Create()).ComputeHash([Text.Encoding]::UTF8.GetBytes($t)) |
     ForEach-Object { $_.ToString('x2') }) -join ''
  ```
- للملف نفسه داخل المستودع المنشور: يمكن التحقق أيضًا بـ `git show <commit>:docs/accounting/reports/ERP-CORE-ACCOUNTING-P7-REPAIR-REPORT.md` (ثم نفس الخطوات أعلاه بعد حذف السطر الأخير).

<!-- P7-SELF-SHA256 (content up to and including the newline before this final line, LF-normalized): 5c7ab24166a142c4cc6209c7e86e8ec354993abd359b4f8e59c278ef0b072284 -->

