# PU-04 — تقرير الحالة النهائية بعد إعادة تشغيل الجهاز

> **نوع الوثيقة:** تقرير تحقّق وإغلاق مبني على أدلة فعلية (كود + بناء + اختبارات + قاعدة بيانات) — **لا تعديل على كود الإنتاج**.
> **تاريخ التقرير:** 2026-10-23
> **المشروع:** `D:\erpfix-pu01` — المصدر في `D:\erpfix-pu01\pkg-source`
> **نطاق التقرير:** استعادة حالة PU-04 بعد انقطاع المهمة، وإكمال التحقق، وإصدار الحالة النهائية. **لم يبدأ PU-05.**
> **حدود التنفيذ:** لم يُلمس الإنتاج/VPS، ولم تُنفَّذ أي عملية Git، ولم تُحذف أي قاعدة بيانات.

---

## 1) ملخص تنفيذي

**PU-04 منفَّذ فعليًا في الكود** (كيانات + هجرة + خدمات + اختبارات)، وليس مجرد تصميم كما تصفه وثائق القرارات. بعد استعادة الحالة أُعيد البناء والتشغيل بالكامل:

| المؤشر | النتيجة الفعلية بعد إعادة التشغيل |
|---|---|
| بناء مشروع الاختبارات | ✅ نجح — **0 خطأ** · 15 تحذيرًا (كلها سابقة ولا تخصّ ملفات PU-04) |
| بناء `ERPSystem.Web` | ✅ نجح (exit 0) — يُثبت أن ملفات Razor المعدَّلة تُصرَّف |
| بناء `ERPSystem.Api` | ✅ نجح (exit 0) |
| اختبارات PU-04 (`~Pu04`) | ✅ **41 ناجح · 0 فاشل · 0 متخطّى** (من 41) — بما فيها اختبارات تكامل حية على PostgreSQL |
| الحزمة الكاملة | ✅ **330 ناجح · 0 فاشل · 2 متخطّى** (من 332) |

**الحالة الفعلية:** البنود الخمسة لمراجعة الإغلاق **منفَّذة تقنيًا ومثبتة باختبارات ناجحة**، لكن هناك ثلاث فجوات لا تسمح بإعلان الإغلاق الكامل:

1. **فجوة اعتماد/حاكمية (جوهري):** وثيقتا الاعتماد (`PU04-SUPPLIER-PAYMENT-ALLOCATION-DESIGN-REVISED-R3.md` §17 بتاريخ 2026-10-22 13:06، و`PU04-OWNER-DECISIONS-R3.md` §24 بتاريخ 2026-10-22 13:08) تُصرّحان بأن المعتمد هو **فقط** D5 · D5-و · D1 + قواعد تحقق، وأن `N-1 · D2 · D2-ج · D3 · D4 · D6 · D7 · D8 · D9 · D10 · D11 · D12 · D13-أ · D13-ب` **غير معتمدة** و«يظل ممنوعًا حتى اعتمادها: لا تنفيذ برمجي · لا هجرة». الكود الموجود **ينفّذ خيارات محددة** لهذه القرارات (D7-ب · N-1 · D13-أ/ب · D2-ج · D4-أ · D12). ⇒ يلزم **تصديق مالك صريح** على هذه الخيارات (أو تسجيل أنها اعتُمدت في جلسة التنفيذ).
2. **فجوة واجهة:** لا توجد واجهة (Web) ولا نقطة API للعكس، ولا لإعادة التوزيع، ولا لتقرير المطابقة. المتاح في الواجهة هو **لوحة تخصيص الدفعة داخل حوار سند الصرف** فقط.
3. **التحقق البصري/المتصفحي لم يُنفَّذ** (لا متصفحات Playwright مثبّتة، وسلسلة اتصال التطبيق تُزوَّد خارجيًا) — التحقق تم برمجيًا فقط.

**التوصية النهائية (انظر §10):** **جاهز للإغلاق التقني مع قرارات محاسبية معلقة.**

---

## 2) استعادة الحالة

### 2.1 المسار الفعلي ومستودع الكود
- مجلد العمل: `D:\erpfix-pu01`، والمصدر: `D:\erpfix-pu01\pkg-source`.
- النسخ الاحتياطية الموجودة في الجذر: `_backup-before-pu02` · `_backup-before-pu03a` · `_backup-before-pu03b` · `_backup-before-pu03c`.
- **لا يوجد مستودع Git** في `pkg-source` (`git status` → `fatal: not a git repository`) ⇒ لا عمليات Git ممكنة أصلًا، ولم تُجرَ أي منها.

### 2.2 آثار آخر نقطة وصل إليها التنفيذ قبل إعادة التشغيل
أحدث الملفات المعدَّلة (كلها 2026-10-22، بين 19:36 و23:40):

| الوقت | الملف |
|---|---|
| 19:36–19:46 | `src/Infrastructure/Data/AppDbContext.cs` · `Configurations/PurchaseReturnConfiguration.cs` · `Application/Services/SupplierAllocationQueries.cs` · `Application/Interfaces/IPaymentVoucherService.cs` · `Application/Security/VoucherAuthorization.cs` · `Web/Permissions/*` · `Api/Security/ApiVoucherAuthorization.cs` · `ReceiptVoucherService.cs` · `DTOs/Purchases/CreatePurchaseReturnDto.cs` |
| 22:16–22:29 | `DTOs/PaymentMethodMap.cs` · `DTOs/Vouchers/PaymentAllocationDtos.cs` · `Interfaces/IPaymentVoucherReversalService.cs` · `Services/PaymentVoucherService.cs` · `Migrations/20261022192835_Pu04SupplierPaymentAllocation(.Designer).cs` + `AppDbContextModelSnapshot.cs` |
| 22:26–22:28 | `AgingReportQueries.cs` · `DTOs/Reports/AgingReportDto.cs` · `SupplierService.Statement.cs` |
| 22:38–23:22 | `AccountingPgFixture.cs` · `Web/.../PaymentVoucherFormDialog.razor` · `DTOs/Purchases/CreatePurchaseInvoiceDto.cs` · `Services/PurchaseInvoiceService.cs` · `DTOs/Vouchers/PaymentVoucherDtos.cs` · `Web/.../PaymentVouchers.razor` · `Services/BankCheckService.cs` · `Services/PurchaseReturnService.cs` · `Services/SupplierReconciliationService.cs` · `Services/PaymentVoucherReversalService.cs` · اختبارات `Pu04*` |
| 23:31–23:40 | `CommercialAcceptanceTests.cs` · `PartyBalancePolicyProbeTests.cs` · `ResidualProbeTests.cs` · `Pu04PurchaseReturnTests.cs` · `Pu04ReconciliationTests.cs` · **`Pu04SupplierAllocationTests.cs` (آخر ملف، 23:40:04)** |

سجلات الجلسة السابقة المتاحة في `%TEMP%`:
- `b1.txt` (22:19) بناء ناجح · `b2.txt` (22:23) بناء الحل الكامل **بخطأ واحد فقط** هو `XA5300` (Android SDK مفقود — خارج نطاق PU-04) · `b3.txt`/`b4.txt`/`b5.txt` (22:24–22:25) «Build succeeded · 0 Warning(s) · 0 Error(s)».
- آخر استدعاء `dotnet` في الجلسة السابقة: سجل `Microsoft.NET.Workload_…_20261022_234831_037.log` عند **23:48:49**، وآخر نشاط عند 23:51.
- **لا يوجد سجل اختبارات محفوظ للجلسة الأخيرة** (23:40–23:51) ⇒ لم يُعتمد أي «نجاح محفوظ»، وأُعيد تشغيل كل شيء (انظر §4).

### 2.3 بيئة قاعدة الاختبار (استعادة، لا إنشاء)
- ملف `%TEMP%\pu01_testpg.pw` (كلمة مرور جلسة ما قبل إعادة التشغيل) **لم يعد يصادق**، ونسخة PG التي كانت على المنفذ 15432 **لم تكن قائمة** بعد إعادة التشغيل (لا `netstat` على 15432، وDocker غير مشغّل، ولا يوجد عنقود بيانات آخر على C:/D:).
- **ما فُعل:** إعادة تشغيل **عنقود التطوير المحمول الموجود مسبقًا** `D:\pgsql_portable\data` (PostgreSQL 17.11، ثنائيات `D:\pgsql_portable\extract\pgsql\bin`) على المنفذ **15432**، بمستخدم `postgres`، وبكلمة المرور الموجودة في `D:\pgsql_portable\pw.txt`.
- **لم يُنشأ أي عنقود/قاعدة جديدة، ولم يُحذف أي شيء.** قواعد العنقود قبل/بعد التشغيل: `ERPSystemDb · postgres · template0 · template1` (لا بقايا قواعد اختبار `erp_a5_*` — الوحدة `AccountingPgFixture` تُنشئ قاعدة مؤقتة وتحذفها في `DisposeAsync`).
- لم يُعدَّل `run-tests.ps1` ولا أي ملف مصدر؛ مُرِّر `ERPSYSTEM_TEST_PG` مباشرة لأوامر التشغيل.

### 2.4 هل توجد تعديلات غير مكتملة أو ملفات مؤقتة؟
- **لا.** البناء نظيف وكل الاختبارات تمر ⇒ لا تعديل نصف مكتمل يُعطّل التصريف أو السلوك.
- ملفات مؤقتة من الجلسة السابقة في `%TEMP%` (`pu01_*`, `b1..b5.txt`, `full-pu0*.txt`) **تُركت كما هي** ولم تُحذف أو تُستبدل.
- الملفات الجديدة التي أنشأها هذا الاستئناف (مخرجات تشغيل موثِّقة، لا تعديل على المصدر): `_pu04_build.log` · `_pu04_web_build.log` · `_pu04_api_build.log` · `_pu04_tests.log` · `_pu04_full_tests.log` في `D:\erpfix-pu01\`.

---

## 3) الأعمال المنجزة

> **ملاحظة صريحة:** بعد استعادة العمل **لم يُجرَ أي إصلاح برمجي**، لأن البحث عن عيوب فعليّة لم يُنتج عيبًا قابلًا لإعادة الإنتاج (البناء نظيف، و41/41 اختبار PU-04 ناجح، و330/332 للحزمة الكاملة). لم يُعدَّل أي ملف مصدر أو اختبار.

### 3.1 الملفات المُثبت تعديلها (PU-04) ووظائفها

| الملف | الوظيفة |
|---|---|
| `src/Domain/Entities/PaymentAllocation.cs` · `PaymentVoucherReversal.cs` | كيانا التخصيص ومستند العكس (مع `Direction` · `ReversalOfAllocationId` · `ReversalDocumentId`) |
| `src/Domain/Enums/PurchaseReturnSettlementType.cs` | `OnAccountCredit = 1` (يخفض الذمم) · `CashRefund = 2` (يتطلب إثبات رد نقد) |
| `src/Infrastructure/Migrations/20261022192835_Pu04SupplierPaymentAllocation.cs` | الهجرة (جداول/أعمدة/قيود/فهارس) — تفصيلها في §5 |
| `src/Infrastructure/Data/Configurations/PaymentAllocationConfiguration.cs` · `PaymentVoucherReversalConfiguration.cs` | إعداد EF + قيود CHECK + فهارس فريدة جزئية |
| `src/Application/Services/SupplierAllocationQueries.cs` | **مصدر الحقيقة الوحيد** لرصيد الفاتورة `R = Total − Paid − ReturnsOnAccount − NetAllocated` · `Available = max(0,R)` · الأهلية · اقتراح FIFO مع حسم القرش |
| `src/Application/Services/PaymentVoucherService.cs` | الترحيل مع التخصيص: قفل المورد `FOR UPDATE` + حارس PU-03 + قفل الفواتير بترتيب حتمي + **`ΣA = Amount` صارم** + قيد واحد فقط (لا قيد للتخصيص) + خفض `Supplier.CurrentBalance` مرة واحدة + مفتاح منع التكرار |
| `src/Application/Services/PaymentVoucherReversalService.cs` | مستند عكس مستقل (D7-ب) + **رفض العكس الجزئي** + `ReallocateAsync` (تصحيح بحت بلا قيد) + قفل المورد/الصفوف + الصلاحية `CanReverse` |
| `src/Application/Services/SupplierReconciliationService.cs` | تقرير المطابقة `ΣR · F2 · F1 المنسوب` + الفروق + `NegativeInvoices` + `UnattributedLedger` — **بلا أي تسوية أو تصفير تلقائي** |
| `src/Application/Services/PurchaseReturnService.cs` | تحقق نوع التسوية (نقدي/على الحساب) + إلزام `RefundReference` للمردود النقدي + قفل المورد + `CurrentBalance` لا يتغير في المردود النقدي |
| `src/Application/Services/SupplierService.Statement.cs` | كشف المورد يعرض الآن **سندات الصرف ومستندات العكس** ورصيدًا افتتاحيًا جاريًا (G5) |
| `src/Application/Services/AgingReportQueries.cs` | أعمار **الموردين** تُبنى على `R` (لا `Total − Paid`) مع عرض الأرصدة السالبة (G4) |
| `src/Application/Services/PurchaseInvoiceService.cs` · `DTOs/Purchases/CreatePurchaseInvoiceDto.cs` | توحيد دلالات `PaymentMethod` للفاتورة عبر `PaymentMethodMap` + تحقق |
| `src/Application/DTOs/PaymentMethodMap.cs` | خريطة صريحة: فاتورة `1=آجل · 2=شيك · 3=نقدي` مقابل سند `1=نقدي · 2=بنكي` |
| `src/Web/Components/Pages/Vouchers/PaymentVoucherFormDialog.razor` | لوحة «تخصيص الدفعة على الفواتير المؤهلة» + زر «اقتراح FIFO (مراجعة إلزامية)» + منع الترحيل عند `Difference != 0` |
| `src/Web/Components/Pages/Vouchers/PaymentVouchers.razor` | حقن `SupplierAllocationQueries` وتمريره للحوار |
| `src/Infrastructure/DependencyInjection.cs` | تسجيل `IPaymentVoucherReversalService` · `SupplierAllocationQueries` · `SupplierReconciliationService` |
| `tests/ERPSystem.Tests/Pu04*.cs` (5 ملفات · 41 اختبارًا) | اختبارات PU-04 (تكامل حي + عقد واجهة) |

### 3.2 إصلاحات جديدة أُجريت بعد استعادة العمل
**لا يوجد.** لم يثبت أي عيب في الكود بعد إعادة التشغيل، فلم يُعدَّل أي ملف مصدر أو اختبار. الجهد أُصرف على الاستعادة والتحقق وإصدار هذا التقرير.

---

## 4) البناء والاختبارات

**البيئة:** `DOTNET_ROOT=D:\dotnet-sdk` (SDK 10.0.1226) · `NUGET_PACKAGES=D:\nuget_cache` · `ERPSYSTEM_TEST_PG=Host=localhost;Port=15432;Database=postgres;Username=postgres;Password=<من pw.txt>`.

### 4.1 نتائج جديدة (نُفِّذت بعد إعادة التشغيل — 2026-10-23)

| # | الأمر | النتيجة الفعلية | ناجح/فاشل/متخطّى | سجل الأدلة |
|---|---|---|---|---|
| 1 | `dotnet build tests\ERPSystem.Tests\ERPSystem.Tests.csproj --nologo -p:SatelliteResourceLanguages=en` | `Build succeeded` · 15 Warning(s) · **0 Error(s)** · 00:01:35.81 · exit 0 | — | `D:\erpfix-pu01\_pu04_build.log` |
| 2 | `dotnet build src\Web\ERPSystem.Web.csproj --nologo …` | exit **0** (تصريف Razor ناجح) | — | `D:\erpfix-pu01\_pu04_web_build.log` |
| 3 | `dotnet build src\Api\ERPSystem.Api.csproj --nologo …` | exit **0** | — | `D:\erpfix-pu01\_pu04_api_build.log` |
| 4 | `dotnet test tests\ERPSystem.Tests\ERPSystem.Tests.csproj --no-build --filter "FullyQualifiedName~Pu04"` | `Passed!` · Duration 17s | **41 / 0 / 0** (من 41) | `D:\erpfix-pu01\_pu04_tests.log` |
| 5 | `dotnet test tests\ERPSystem.Tests\ERPSystem.Tests.csproj --no-build` (الحزمة الكاملة) | `Passed!` · Duration 2m 25s | **330 / 0 / 2** (من 332) | `D:\erpfix-pu01\_pu04_full_tests.log` |

- **التحذيرات الـ15** كلها سابقة ولا تخصّ ملفات PU-04: `CS8618` في `BankCheckDtos` · `CS8602` في `BankCheckService` · `CS8601/CS8603` في `ShelfService` · `SYSLIB0057` في `HealthChecks` · `CS8604` ×3 في `BackupService` · `xUnit2013` ×2 في `VoucherReportPgTests` · `EF1001` في `ResidualProbeTests`.
- **الاختباران المتخطّيان** (سابقان ومقصودان، ليسا فشلًا): `AccountingProofSeedTests.Run_full_proof_dataset` و`GateRestoreSeedTests.Seed_gate_database_with_real_posted_documents`.
- **`Skipped: 0` في تشغيل PU-04 يعني أن اختبارات `LivePgFact` الحيّة نُفِّذت فعلًا** على PostgreSQL (لا تخطّيًا) ⇒ دليل حقيقي على سلامة التخصيص والعكس والمردودات على قاعدة حقيقية بعد تطبيق الهجرات.

### 4.2 توزيع اختبارات PU-04 (41 اختبارًا)

| الملف | العدد | التغطية |
|---|---|---|
| `Pu04SupplierAllocationTests.cs` | 21 | تخصيص كلي/جزئي/متعدد الفواتير · رفض المبلغ غير الموزَّع · رفض فاتورة مورد آخر · رفض فاتورة غير مرحلة/نقدية · تجاوز المتاح · تعدد الصفوف الموجبة · التقريب (ثلاثة أقسام) · منع الترحيل المزدوج · العكس الكلي/رفض الجزئي/إعادة التوزيع/الصلاحيات/الازدواج بالمفتاح/رفض عكس تخصيص سند آخر |
| `Pu04ReconciliationTests.cs` | 5 | مطابقة الثلاثة أرصدة · ثباتها مع سندات متعددة · العكس الكلي بلا ازدواج · عرض الرصيد السالب بلا تصفير · القيد اليدوي «غير منسوب» بلا تسوية تلقائية |
| `Pu04PurchaseReturnTests.cs` | 5 | مردود آجل يخفض الالتزام · مردود نقدي بإثبات لا يغيّر الرصيد · رفض مردود نقدي بلا إثبات · رفض خلط الأنواع |
| `Pu04PaymentMethodMappingTests.cs` | 6 | تثبيت قيم الخريطة · دلالة 1/2/3 للفاتورة · رفض قيمة غير معروفة · ربط 1/2 بحساب الصندوق/البنك للسند |
| `Pu04PaymentVoucherUiContractTests.cs` | 4 | عقد حوار التخصيص · حقن الاستعلامات في الصفحة · تدفق مسودة←اعتماد←ترحيل بتخصيص كامل (تكامل حي) · وسم حالة «معكوس» |

### 4.3 النتائج التاريخية المحفوظة (للتمييز فقط — لم تُستخدم كدليل نجاح)
- `%TEMP%\b1.txt` (22:19): بناء ناجح · `b3/b4/b5.txt` (22:24–22:25): «Build succeeded · 0 Warning(s) · 0 Error(s)» · `b2.txt` (22:23): بناء الحل الكامل بخطأ `XA5300` (Android SDK) فقط.
- هذه سجلات **بناء** لا **اختبارات**، ولا يوجد أي سجل اختبار محفوظ للجلسة الأخيرة ⇒ أُعيد تشغيل كل الاختبارات في §4.1.

### 4.4 أخطاء بيئية واجهت التشغيل
| الحالة | السبب المؤكد | الأثر |
|---|---|---|
| خادم الاختبار على المنفذ 15432 غير قائم بعد إعادة التشغيل، و`pu01_testpg.pw` لا يصادق | **مؤكد:** إعادة تشغيل الجهاز أوقفت نسخة PG المؤقتة السابقة، ولم يبقَ لها مسار بيانات على الأقراص | عولج بإعادة استخدام عنقود التطوير المحمول الموجود (`D:\pgsql_portable`) على 15432 |
| `docker ps` | **مؤكد:** Docker Desktop غير مشغّل (`dockerDesktopLinuxEngine` غير موجود) | لا تأثير — لم تُستخدم حاويات |
| بناء الحل الكامل (بما فيه Mobile) | **مؤكد:** `XA5300` — Android SDK/JDK غير مهيأ على الجهاز | خارج نطاق PU-04؛ بناء `Web` و`Api` و`Tests` ناجح |

---

## 5) قاعدة البيانات

**قاعدة البيانات المستخدمة:** PostgreSQL **17.11** على `127.0.0.1:15432` (عنقود التطوير المحمول `D:\pgsql_portable\data`)، بمستخدم `postgres`. **لم تُستخدم أي قاعدة إنتاج، ولم يُطبَّق أي ترحيل على قاعدة قائمة.**

### 5.1 الهجرة `20261022192835_Pu04SupplierPaymentAllocation`

| العنصر | التفصيل (من ملف الهجرة) |
|---|---|
| جدول جديد | `PaymentVoucherReversals` — `Id` · `ReversalNumber`(30، فريد) · `OriginalPaymentVoucherId` · `SupplierId` · `VoucherDate` · `Status` · `Amount numeric(18,2)` · `CashOrBankAccountId` · `PayableAccountId` · `Reason`(500) · `JournalEntryId` · `IdempotencyKey` · `RowVersion` · حقول التدقيق |
| جدول جديد | `PaymentAllocations` — `Id` · `PaymentVoucherId` · `PurchaseInvoiceId` · `SupplierId` · `Amount numeric(18,2)` · `Direction smallint` · `ReversalOfAllocationId` · `ReversalDocumentId` · `AllocatedAt` · `Note`(500) · حقول التدقيق |
| قيود CHECK | `ck_alloc_amount_positive` = `"Amount" > 0` · `ck_alloc_direction` = `"Direction" IN (1,-1)` · `ck_alloc_reversal_shape` = `(("Direction" = -1) = ("ReversalOfAllocationId" IS NOT NULL))` |
| مفاتيح أجنبية | كلها `Restrict`: السند · الفاتورة · المورد · `ReversalOfAllocationId` (ذاتي) · `ReversalDocumentId` · حسابا الصندوق/الذمم على مستند العكس |
| فهارس فريدة جزئية | `UX_PaymentVouchers_IdempotencyKey` · `UX_PaymentVoucherReversals_IdempotencyKey` · `UX_PaymentVoucherReversals_JournalEntry` · `UX_PaymentVoucherReversals_Number` · `UX_PaymentVoucherReversals_Original` |
| فهارس غير فريدة | `IX_PaymentAllocations_Invoice_Direction` · `_ReversalDocumentId` · `_ReversalOf` · `_Supplier_Date` · `_Voucher` · وفهارس `PaymentVoucherReversals` (CashOrBankAccountId · PayableAccountId · SupplierId) |
| أعمدة مضافة | `PurchaseReturns.SettlementType int NOT NULL DEFAULT 1` · `PurchaseReturns.RefundReference varchar(200) NULL` · `PaymentVouchers.IdempotencyKey uuid NULL` |
| `Down()` | يحذف الجدولين/الفهارس/الأعمدة (هجرة قابلة للتراجع) |

### 5.2 دليل قابلية التطبيق (لا ادّعاء بغير دليل)
- **دليل مباشر:** `AccountingPgFixture.InitializeAsync` يُنشئ قاعدة مؤقتة `erp_a5_<10>` ثم يستدعي `ctx.Database.MigrateAsync()` — ونجاح **41/41** اختبار PU-04 (بـ`Skipped: 0`) يعني أن **كل** الهجرات بما فيها هجرة PU-04 طُبِّقت بنجاح على قاعدة PostgreSQL حقيقية نظيفة، ثم عُمل عليها تخصيص/عكس/مردودات فعلية.
- **دليل التنظيف:** بعد التشغيل، قواعد العنقود = `ERPSystemDb · postgres · template0 · template1` ⇒ القاعدة المؤقتة حُذفت بواسطة `DisposeAsync` (`DROP DATABASE … WITH (FORCE)`) ولم تبقَ بقايا `erp_a5_*`.
- **لم يُتحقّق** من تطبيق الهجرة على قاعدة الإنتاج/التطوير القائمة، ولم يُحاوَل ذلك.

### 5.3 حالة الترحيلات المطبَّقة فعليًا
- لم تُقرأ قائمة `__EFMigrationsHistory` من أي قاعدة قائمة (لم تُفتح قاعدة التطوير `ERPSystemDb`).
- الحكم الوحيد الموثَّق: الهجرة **قابلة للتطبيق على قاعدة نظيفة** (مثبت أعلاه). أي ادّعاء بأنها «طُبِّقت على قاعدة الإنتاج» **غير مسند** ولذلك لم يُذكر.

---

## 6) مراجعة الإغلاق النهائية

### 6.1 البند 1 — العكس الجزئي مقابل إعادة توزيع التخصيصات

| الوجه | التفصيل |
|---|---|
| **الحالة** | ✅ **مكتمل ومثبت بالأدلة** (خدمة + اختبارات) · ⚠️ **بلا واجهة/API** |
| **الأدلة (كود)** | `PaymentVoucherReversalService.ReverseCoreAsync` يرفض العكس الجزئي صراحةً (`total != voucher.Amount` ⇒ استثناء يوجّه إلى `Reallocate`) · العكس الكلي: صفوف `Direction = -1` مرتبطة بـ`ReversalOfAllocationId` + `ReversalDocumentId` + قيد عكسي متوازن (مدين الصندوق/البنك · دائن 2200) + إعادة الرصيد `supplierBalance + total` + حالة الأصل `Reversed` · منع عكس ثانٍ لنفس السند · منع عكس صف لا يتبع السند · منع تجاوز `Σعكوس الصف ≤ الأصل` · `ReallocateAsync`: صفوف سحب سالبة + صفوف إضافة موجبة **بلا أي قيد محاسبي**، مع شرط `Σإعادة التوزيع == Σالمسحوب` وأهلية الفاتورة الوجهة |
| **الأدلة (اختبارات)** | `Reverse_rejects_partial_amount` · `Reverse_full_restores_balances_and_marks_voucher_reversed` · `Reallocate_moves_amount_between_invoices_without_new_entry` · `Reallocate_rejects_amount_over_original` · `Reallocate_rejects_mismatched_totals` · `Reallocate_requires_reverse_permission` · `Reverse_requires_reverse_permission` · `Reverse_is_idempotent_with_same_key` · `Reverse_twice_without_key_is_rejected` · `Reverse_rejects_allocation_from_another_voucher` — **كلها ناجحة** |
| **مشكلة متبقية** | لا واجهة ولا نقطة API تستدعي `ReverseAsync`/`ReallocateAsync` (تسجيل DI موجود في `DependencyInjection.cs:252`، لكن لا مستهلك). كما أن **D7-ب** و**D13-ب** غير معتمدين في وثيقة القرارات (انظر §7) |

### 6.2 البند 2 — مردودات المشتريات والتسويات النقدية

| الوجه | التفصيل |
|---|---|
| **الحالة** | ✅ **مكتمل ومثبت بالأدلة** للمسارين `OnAccountCredit` و`CashRefund` · ⚠️ `PendingClaim` (M3/M4) **غير مدعوم** |
| **الأدلة (كود)** | `PurchaseReturnService`: فاتورة نقدية ⇒ تُفرض `CashRefund` **مع إلزام `RefundReference`** وإلا رفض · فاتورة آجلة ⇒ تُفرض `OnAccountCredit` فقط · `debitAccount = CashRefund ? 1100 : 2200` · `Supplier.CurrentBalance` يُخفَّض **فقط** في غير `CashRefund` وبعد قفل الصف `FOR UPDATE` · `SupplierAllocationQueries.GetReturnsOnAccountAsync` يطرح **فقط** مردودات `Posted + OnAccountCredit` من `R` |
| **الأدلة (اختبارات)** | `On_account_return_reduces_payable_and_supplier_balance` · `Cash_return_with_evidence_does_not_change_supplier_or_payable` · `Cash_return_without_evidence_is_rejected` · `Cash_invoice_with_on_account_settlement_is_rejected` · `On_account_invoice_with_cash_settlement_is_rejected` · `Negative_invoice_residual_is_reported_not_zeroed` — **كلها ناجحة** |
| **مشكلة متبقية** | لا حقل/حالة للمطالبة (M3) ولا «مردود مرحَّل لم تُحدَّد تسويته» (M4) — يتطلب قرار D2-ج وحساب مطالبة غير موجود في البذر (`1207`) |

### 6.3 البند 3 — تضارب دلالات `PaymentMethod`

| الوجه | التفصيل |
|---|---|
| **الحالة** | ✅ **مكتمل ومثبت بالأدلة** |
| **الأدلة (كود)** | `PaymentMethodMap` يثبّت فضاءين صريحين: **فاتورة المشتريات** `1=آجل · 2=شيك · 3=نقدي` مقابل **سند الصرف/القبض** `1=نقدي · 2=بنكي`؛ `CreatePurchaseInvoiceDto.PaymentOnAccount/PaymentCheck/PaymentCash` تُشتق منه؛ `PurchaseInvoiceService` يتحقق من الصلاحية ويشترط بيانات الشيك عند `2`؛ `PaymentVoucherService.ValidateDraftInput` يقبل `1|2` فقط؛ **القيم الرقمية لم تتغير** (توافق خلفي كامل) |
| **الأدلة (اختبارات)** | `Map_pins_invoice_and_voucher_value_spaces` · `Invoice_payment_method_one_is_on_account_not_cash` · `Invoice_payment_method_two_registers_issued_check` · `Invoice_payment_method_three_is_cash_and_fully_paid` · `Invoice_rejects_unknown_payment_method` · `Voucher_payment_method_maps_to_cash_or_bank_account` — **كلها ناجحة** |
| **مشكلة متبقية** | لا شيء وظيفيًا. يبقى **توثيقيًا** أن `PaymentMethod` في فاتورة المشتريات يعني «طريقة سداد الفاتورة» بينما في السند يعني «وسيلة الصرف» — وهو موثَّق الآن في `PaymentMethodMap` |

### 6.4 البند 4 — المصالحة بين `ΣR` و`F2` و`F1`

| الوجه | التفصيل |
|---|---|
| **الحالة** | ✅ **مكتمل ومثبت بالأدلة** (خدمة) · ⚠️ **بلا واجهة/تقرير مرئي** |
| **الأدلة (كود)** | `SupplierReconciliationService.GetAsync`: `ΣR` = مجموع متبقيات الفواتير الآجلة · `F2` = `Suppliers.CurrentBalance` · `F1 المنسوب` = `Σ فواتير آجلة − مردودات M1 − سندات (Posted/Reversed) + مستندات عكس` · الفروق `ΣR−F2` و`F2−F1` تُعرض · `NegativeInvoices` يعدّ الأرصدة السالبة · `UnattributedLedger = رصيد 2200 الكلي − المنسوب` · **لا تسوية ولا تصفير تلقائي ولا كتابة على قاعدة البيانات** |
| **الأدلة (اختبارات)** | `Partial_payment_matches_all_three_balances` · `Multiple_invoices_payments_stay_consistent` · `Full_voucher_reversal_restores_balances_without_double_counting` · `Negative_invoice_residual_is_reported_not_zeroed` · `Manual_entry_on_payable_is_reported_as_unattributed` — **كلها ناجحة** |
| **مشكلة متبقية** | `F1` مُشتق **بنوع المستند** لا ببُعد طرف على بنود القيد (لا يوجد `PartyId` في `JournalEntryLine`) ⇒ الأرصدة الافتتاحية والقيود اليدوية والشيكات تظهر في «غير المنسوب». لا واجهة لعرض التقرير (لا Web ولا API). يتطلب قرارًا حول إسناد قيود الأستاذ للمورد (§7) |

### 6.5 البند 5 — واجهة سندات دفع الموردين

| الوجه | التفصيل |
|---|---|
| **الحالة** | ⚠️ **منفذ جزئيًا** — التحقق **برمجي فقط**؛ **لا فحص بصري/متصفحي** |
| **ما تحقّق برمجيًا** | `PaymentVoucherFormDialog.razor` يحوي: عنوان «تخصيص الدفعة على الفواتير المؤهلة» (س63) · زر «اقتراح FIFO (مراجعة إلزامية)» يستدعي `SuggestFifoAsync` (س64/س179) · حقل `MudNumericField` لكل فاتورة عبر `AllocationOf(context.InvoiceId)` (س89) · عرض «مبلغ السند / إجمالي التخصيص / الفرق» (س97) و**منع الترحيل عند `Difference != 0m`** (س99) · رسالة عند `_eligible.Count == 0` (س67) · `PostAsync(voucher.Id, allocationInputs)` (س308) · `PaymentVouchers.razor` يحقن `SupplierAllocationQueries` ويمرّره (س9/س220/س239) · مشروع Web **يُصرَّف بنجاح** |
| **الأدلة (اختبارات)** | `Dialog_exposes_allocation_panel_and_blocks_unbalanced_posting` · `Page_passes_allocation_queries_and_injects_service` · `Ui_flow_draft_approve_post_with_allocations_succeeds` (تدفق كامل: فواتير مؤهلة ← مسودة ← اعتماد ← ترحيل بتخصيص ← `R = 0`) · `Reversed_status_is_labelled_for_the_ui` — **كلها ناجحة** |
| **ما لم يتحقّق** | ❌ **لم يُفتح متصفح ولم يُشغَّل التطبيق**: لا متصفحات Playwright مثبّتة (`%LOCALAPPDATA%\ms-playwright` غير موجود)، وسلسلة اتصال `appsettings*.json` فارغة (`ConnectionStrings:DefaultConnection = ""`) وتُزوَّد خارجيًا. **لا ادّعاء بنجاح فحص بصري أو اختبار واجهة حقيقي.** |
| **مشكلة متبقية** | لا واجهة للعكس/إعادة التوزيع/تقرير المطابقة · ولا نقطة API لأي منها (فحص `src/Api` أعاد صفرًا لكل من `SupplierReconciliation` و`PaymentVoucherReversal` و`Reallocate` و`PaymentAllocation`) |

### 6.6 تصنيف البنود الخمسة (ملخّص)

| # | البند | التصنيف |
|---|---|---|
| 1 | العكس الجزئي وإعادة التوزيع | **مكتمل ومثبت بالأدلة** — مع فجوة واجهة وفجوة اعتماد (D7-ب/D13-ب) |
| 2 | مردودات المشتريات | **مكتمل ومثبت بالأدلة** (M1/M2) — و**متوقف بسبب قرار محاسبي** في (M3/M4) |
| 3 | `PaymentMethod` | **مكتمل ومثبت بالأدلة** |
| 4 | المصالحة `ΣR · F2 · F1` | **مكتمل ومثبت بالأدلة** (خدمة) — و**متوقف بسبب قرار محاسبي** في إسناد قيود الأستاذ للمورد |
| 5 | واجهة المستخدم | **منفذ جزئيًا** — برمجيًا فقط؛ الفحص البصري **تعذّر التحقق منه** |

---

## 7) القرارات المحاسبية المعلقة

### 7.1 قرارات معلقة رسميًا (نصّها في وثيقة القرارات)
المصدر: `PU04-OWNER-DECISIONS-R3.md` §24.5 و`…-REVISED-R3.md` §17.4 (كلاهما بتاريخ 2026-10-22): «**غير معتمد (يحتاج قرار):** N-1 · D2 · D2-ج · D3 · D4 · D6 · D7 · D8 · D9 · D10 · D11 · D12 · D13-أ · D13-ب · ومكان حفظ نية التوزيع (و-1/و-2/و-3)».

| القرار | السؤال المحاسبي/المعماري | الحالة في الكود | ما يلزم |
|---|---|---|---|
| **D7** | آلية عكس سند الصرف المرحَّل | نُفِّذ الخيار **(ب)** `PaymentVoucherReversal` مستقلًا | تصديق المالك على (ب) |
| **N-1** | مفتاح منع تكرار الإنشاء | نُفِّذ (حقل + فهرس فريد جزئي + فحص تطابق المفتاح) | تصديق |
| **D13-أ / D13-ب** | تعدد الصفوف الموجبة لنفس (سند، فاتورة) · تعدد العكوس الجزئية لصف واحد | نُفِّذ (أ-2) و(ب-2) | تصديق |
| **D4** | `PaidAmount` اشتقاقي أم محدَّث | نُفِّذ الاشتقاق (أ) — `PaidAmount` لا يُحدَّث بالسند | تصديق |
| **D2 + D2-ج** | عرض الرصيد السالب + تصنيف تسوية المردود | نُفِّذ (ب) للعرض + `SettlementType` بقيمتين فقط | اعتماد بقية الأنواع + **حساب المطالبة** |
| **D12** | شكل تقرير المطابقة والإسناد | نُفِّذت الخدمة (بلا واجهة) | اعتماد سياسة الإسناد والعرض |
| **D3 · D6 · D8 · D9 · D10 · D11** | التوزيع · الأثر التاريخي · حسم القرش · المبلغ غير الموزَّع · صفوف التصحيح · توثيق النية في الواجهة | نُفِّذت جزئيًا ضمن التخصيص/إعادة التوزيع | اعتماد صريح + تسجيل «مكان حفظ النية» |
| **مكان حفظ نية التوزيع (و-1/و-2/و-3)** | أين تُحفظ النية | نُفِّذ (و-1): تُمرَّر مع طلب الترحيل فقط ولا تُحفظ صفوف قبل الترحيل | تسجيل الخيار رسميًا |

### 7.2 نقاط محاسبية أخرى غير محسومة (ثبتت من الكود والوثائق)

| # | النقطة | الدليل | القرار المطلوب |
|---|---|---|---|
| 1 | **حساب المطالبة (M3/M4)** | `PU04-…-REVISED-R3.md` F-19: لا حساب `1207` (سلف موردين) ولا حساب مطالبات في البذر؛ وD2-ج يطرح «رصيد مدين على 2200 أم حساب مطالبات مستقل؟» | هل يُستخدم `2200` برصيد مدين للمطالبة، أم يُنشأ حساب مطالبات مستقل؟ وهل يُعرض كبند مستقل في كشف المورد؟ |
| 2 | **سياسة ضريبة المدخلات** | معادلة `R` تستخدم `TotalAmount` **شاملًا الضريبة المرسملة** (`T_i` موصوف صراحةً بذلك)، ولا يوجد في نطاق PU-04 أي قرار بفصل ضريبة مدخلات قابلة للاسترداد عن الوعاء | هل تُفصل ضريبة المدخلات عن مبلغ الفاتورة لأغراض التخصيص/المطابقة؟ أم يبقى الوعاء شاملًا الضريبة؟ |
| 3 | **إسناد قيود الأستاذ العام للمورد** | `JournalEntryLine` لا يحمل بُعد طرف؛ لذلك `F1` في المطابقة يُشتق بنوع المستند، والأرصدة الافتتاحية والقيود اليدوية والشيكات تقع في `UnattributedLedger` | هل يُضاف بُعد طرف (`SupplierId`) لبنود القيد ليمكن الإسناد الدقيق؟ |
| 4 | **مصير الفروق القائمة** | `ΣR ≠ F2 ≠ F1` مُعترف به ومقصود (لا تسوية تلقائية) | هل تُعالَج الفروق التاريخية بقيود تسوية معتمدة، أم تبقى معروضة فقط؟ |
| 5 | **الأرصدة الموجبة بلا فواتير** | §17.3 يوثّق تعارضًا: رصيد موجب من أرصدة افتتاحية/قيود يدوية/مردودات **بلا فواتير** كان يسمح بالدفع، وصار مرفوضًا بعد D1 | هل يُعتمد استثناء موثَّق لحالات مشروعة؟ |
| 6 | **مردودات المشتريات القائمة غير مصنَّفة** | `SettlementType` أُضيف بقيمة افتراضية `1 = OnAccountCredit` | هل القيمة الافتراضية صحيحة للمردودات التاريخية، أم يلزم تصنيف يدوي؟ |
| 7 | **عرض العكس/إعادة التوزيع/المطابقة في الواجهة** | لا مستهلك في Web/Api للخدمات الثلاث | قرار نطاق: هل تُبنى الواجهة في PU-04 أم في قسيمة لاحقة؟ |

---

## 8) معايير القبول

| # | معيار القبول | الحالة | الدليل |
|---|---|---|---|
| 1 | توازن قيود اليومية (لا قيد غير متوازن) | ✅ **ناجح ومثبت** | قيد السند: مدين 2200 / دائن 1100-1101 بمبلغ السند · قيد العكس معكوسه · `Σ` التخصيصات **لا يُنشئ قيدًا** · الحزمة الكاملة 330/0/2 بما فيها `LedgerInvariants` و`AccountingCycleIntegrityTests` |
| 2 | تخصيص كامل مبلغ سند الدفع (`ΣA = Amount`) | ✅ **ناجح ومثبت** | `PostCoreAsync` س399-403 يرفض أي فرق · `Post_rejects_unallocated_amount` · `Post_rounding_third_split_sums_exactly` |
| 3 | منع تجاوز الرصيد المتاح (فاتورة ومورد) | ✅ **ناجح ومثبت** | حارس PU-03 (مورد، `FOR UPDATE`) **لم يُضعَف** + فحص `R_i` التراكمي · `Post_rejects_exceeding_invoice_available_even_if_supplier_balance_is_enough` · `Post_rejects_invoice_of_another_supplier` |
| 4 | منع الترحيل المكرر | ✅ **ناجح ومثبت** | فحص `JournalEntryId/Status` + `IdempotencyKey` · `Post_twice_does_not_duplicate_entry_or_allocation` · `Reverse_is_idempotent_with_same_key` · `Reverse_twice_without_key_is_rejected` |
| 5 | العكس الكلي والجزئي | ✅ **ناجح ومثبت** للكلي · ✅ **رفض الجزئي مثبت** · ⚠️ لا واجهة | §6.1 |
| 6 | سلامة أرصدة الموردين تحت التزامن | ✅ **ناجح ومثبت** | قفل `Suppliers … FOR UPDATE` في الترحيل والعكس والمردود + `RowVersion` + `ConcurrencyHelper` |
| 7 | معالجة مردودات المشتريات | ✅ **ناجح ومثبت** (M1/M2) · ❌ **غير مستوفى** (M3/M4 مطالبة) | §6.2 + §7.2(1) |
| 8 | تقارير أعمار الديون | ✅ **ناجح ومثبت** للموردين (تُبنى على `R` وتُعرض السالبة) | `AgingReportQueries` س52-69 |
| 9 | كشف حساب المورد | ✅ **ناجح ومثبت** (فواتير + مردودات + **سندات صرف** + **مستندات عكس** برصيد جارٍ) | `SupplierService.Statement.cs` س36-60 |
| 10 | المصالحة بين الفواتير ورصيد المورد والأستاذ العام | ✅ **ناجح ومثبت** (تقرير للعرض، بلا تسوية) · ⚠️ **بلا واجهة** | §6.4 |
| 11 | تطبيق الهجرة على قاعدة نظيفة | ✅ **ناجح ومثبت** | §5.2 |
| 12 | سلامة البناء (Web/Api/Tests) | ✅ **ناجح ومثبت** | §4.1 (0 خطأ) |
| 13 | التحقق البصري/المتصفحي لدورة سند الدفع | ❌ **غير متحقق منه** (بيئة) | §6.5 |
| 14 | وجود واجهة للعكس/إعادة التوزيع/المطابقة | ❌ **غير مستوفى** | §6.1 · §6.4 · §6.5 |
| 15 | اعتماد القرارات المحاسبية المانعة (D7 · N-1 · D4 · D13 · D2-ج · D12 …) | ⏸️ **متوقف بقرار مطلوب** | §7.1 |

---

## 9) المخاطر المتبقية

| # | الخطر | الأثر المحتمل | التخفيف القائم / المطلوب |
|---|---|---|---|
| 1 | **تنفيذ قرارات غير معتمدة رسميًا** (D7-ب · N-1 · D4-أ · D13-أ/ب · D2-ج · D12) | إن لم يُصدَّق المالك عليها، فهي تغييرات سلوكية/مخطّطية قائمة بلا سند اعتماد — ومخالفة لنصّ «يظل ممنوعًا حتى الاعتماد: لا تنفيذ برمجي · لا هجرة» | **مطلوب:** تصديق مالك موثَّق، أو التراجع عن الخيارات غير المصادَق عليها |
| 2 | **قيمة `SettlementType` الافتراضية = 1** على المردودات التاريخية | تصنيف خاطئ لمردود نقدي قديم ⇒ أثر على `R` و`F2` | **مطلوب:** قرار حول تصنيف المردودات القائمة |
| 3 | **`F1` مُشتق بنوع المستند لا ببُعد طرف** | قيود يدوية/افتتاحية/شيكات تبقى «غير منسوبة» ⇒ مطابقة غير دقيقة على مستوى المورد | **مطلوب:** قرار إضافة بُعد طرف لبنود القيد |
| 4 | **الرصيد السالب على الفاتورة يُعرض ولا يُسوّى** | تقارير المطابقة ستُظهر فروقًا دائمة (مقصود ومعلن، لكن يجب أن يفهمه المستخدم) | عرض بوضوح — الواجهة غير موجودة بعد |
| 5 | **غياب واجهة العكس/إعادة التوزيع/المطابقة** | لا يمكن تشغيل الوظائف من النظام إلا برمجيًا ⇒ خطر تشغيلي حقيقي | **مطلوب:** قرار نطاق لبناء الواجهة |
| 6 | **عدم تنفيذ أي فحص بصري/متصفحي** | أخطاء عرض/تفاعل في حوار التخصيص قد تمر دون كشف | **مطلوب:** بيئة متصفح + بيانات اختبار ثم اختبار E2E |
| 7 | **الاعتماد على عنقود PG محمول على 15432** | تكرار الانقطاع بعد كل إعادة تشغيل | توثيق خطوة تشغيل العنقود قبل الاختبارات الحيّة |
| 8 | **`XA5300` في بناء الحل الكامل (Mobile)** | تعذّر بناء الحزمة الكاملة بلا Android SDK/JDK | خارج PU-04؛ لا يمسّ Web/Api/Tests |

**إفصاح:** لم يُخفَ أي إخفاق — لا اختبارات فاشلة، والاختباران المتخطّيان سابقان ومقصودان، والبنود غير المستوفاة مذكورة صراحةً في §6 و§8.

---

## 10) التوصية النهائية

## ⏸️ جاهز للإغلاق التقني مع قرارات محاسبية معلقة

**التفسير المبني على الأدلة:**

- **الجزء التقني جاهز:** البناء نظيف (0 خطأ) · **41/41** اختبار PU-04 ناجح (بلا تخطٍّ) · **330/332** للحزمة الكاملة بلا فشل · الهجرة قابلة للتطبيق على قاعدة نظيفة · حارس PU-03 **لم يُضعَف** · لا تسوية ولا تصفير تلقائي · لا تكرار للقيود · القيود متوازنة.
- **لكن الإغلاق الكامل غير ممكن الآن** للأسباب التالية:
  1. **قرارات مانعة ما زالت غير معتمدة رسميًا** (D7 · N-1 · D4 · D13-أ/ب · D2-ج · D12 · وD3/D6/D8–D11 ومكان حفظ النية) بينما الكود ينفّذ خيارات محددة لها ⇒ **يلزم تصديق مالك صريح** (أو تسجيل أن الاعتماد حدث في جلسة التنفيذ).
  2. **بنود غير مستوفاة وظيفيًا:** حساب المطالبة (M3/M4) · واجهة العكس/إعادة التوزيع/المطابقة · الفحص البصري لدورة سند الدفع.
- **لم يُعلن «جاهز للإغلاق» الكامل** لأن هناك قرارات محاسبية جوهّرية غير محسومة، واختبارًا حرجًا (الواجهة عبر المتصفح) **لم يُنفَّذ**.
- **ولم يُعلن «غير جاهز للإغلاق»** لأن الأساس التقني سليم ومثبت بالاختبارات، ولا يوجد خلل محاسبي مُثبت في السلوك المنفَّذ.

---

## 11) تأكيد حدود التنفيذ

| الحد | الحالة |
|---|---|
| الإنتاج / VPS | ✅ **لم يُلمس** — لا اتصال بأي قاعدة إنتاج، ولا نشر، ولا حاوية إنتاج |
| حاويات الإنتاج | ✅ **لم تُلمس** (Docker لم يكن مشغّلًا أصلًا) |
| عمليات Git (`reset` · `checkout` · `restore` · `clean` · `add` · `commit` · `push`) | ✅ **لم تُنفَّذ ولا واحدة** — و`pkg-source` ليس مستودع Git أصلًا |
| حذف قواعد بيانات أو بيانات قائمة | ✅ **لم يُحذف شيء** — القاعدة المؤقتة الوحيدة حذفتها الوحدة نفسها (`DisposeAsync`) |
| تعديل ملفات مصدر/اختبارات/هجرات | ✅ **صفر تعديل** — لم يثبت أي عيب |
| إعادة تنفيذ PU-04 من الصفر | ✅ **لم يحدث** — لم يُنشأ كيان/خدمة/هجرة جديدة |
| بدء PU-05 | ✅ **لم يبدأ** |
| إعادة كتابة ملفات مكتملة لتوحيد الأسلوب | ✅ **لم يحدث** |
| تعديل الإعداد/البيئة | إعادة تشغيل عنقود PG التطويري المحمول الموجود على 15432 (ضروري للاختبارات الحيّة) — **لم يُنشأ عنقود ولا قاعدة جديدة** |
| إيقاف العمل بسبب نقطة محاسبية واحدة | ✅ **لم يحدث** — أُكمل كل ما هو مستقل عن القرارات المحاسبية |

---

### ملحق: الأوامر الفعلية المستخدمة

```powershell
# تهيئة البيئة
$env:DOTNET_ROOT='D:\dotnet-sdk'; $env:PATH='D:\dotnet-sdk;'+$env:PATH; $env:NUGET_PACKAGES='D:\nuget_cache'
Set-Location 'D:\erpfix-pu01\pkg-source'

# البناء
& 'D:\dotnet-sdk\dotnet.exe' build 'tests\ERPSystem.Tests\ERPSystem.Tests.csproj' --nologo -p:SatelliteResourceLanguages=en
& 'D:\dotnet-sdk\dotnet.exe' build 'src\Web\ERPSystem.Web.csproj' --nologo -p:SatelliteResourceLanguages=en
& 'D:\dotnet-sdk\dotnet.exe' build 'src\Api\ERPSystem.Api.csproj' --nologo -p:SatelliteResourceLanguages=en

# تشغيل عنقود PostgreSQL التطويري الموجود (لا إنشاء جديد)
& 'D:\pgsql_portable\extract\pgsql\bin\pg_ctl.exe' -D 'D:\pgsql_portable\data' -o '-p 15432' -l 'D:\pgsql_portable\pg15432.log' -w start

# اختبارات PU-04
$env:ERPSYSTEM_TEST_PG='Host=localhost;Port=15432;Database=postgres;Username=postgres;Password=<من D:\pgsql_portable\pw.txt>'
& 'D:\dotnet-sdk\dotnet.exe' test 'tests\ERPSystem.Tests\ERPSystem.Tests.csproj' --nologo --no-build -p:SatelliteResourceLanguages=en --filter 'FullyQualifiedName~Pu04'

# الحزمة الكاملة
& 'D:\dotnet-sdk\dotnet.exe' test 'tests\ERPSystem.Tests\ERPSystem.Tests.csproj' --nologo --no-build -p:SatelliteResourceLanguages=en
```

**لا اعتماد تجاري · لا نشر · لا commit/push · لا بدء PU-05.**
