using System.Data;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.Interfaces;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERPSystem.Application.Services;

/// <summary>
/// Implements journal entry (القيد المحاسبي) logic — the heart of double-entry accounting.
///
/// لماذا يعتبر هذا الجزء حاسماً؟
/// كل عملية تجارية يجب أن تتحول إلى قيد متوازن (المدين = الدائن) لضمان توازن
/// الميزانية دائماً. الترحيل هنا "يُجهّز" القيد في السياق المشترك دون حفظ فوري،
/// بحيث يحفظه المتصل (مثل خدمة فاتورة البيع) في نفس SaveChanges مع مستنده ذاته —
/// وهذا يمنحنا الذرية (Atomicity): إما أن يُحفظ المستند مع قيوده معاً، أو لا يُحفظ شيء.
/// </summary>
public partial class JournalEntryService : IJournalEntryService
{
    private readonly DbContext _context;

    public JournalEntryService(DbContext context)
    {
        _context = context;
    }

    public async Task<List<JournalEntryDto>> GetEntriesAsync()
    {
        var entries = await _context.Set<JournalEntry>()
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .OrderByDescending(e => e.EntryDate)
            .ThenByDescending(e => e.CreatedAt)
            .ToListAsync();

        return entries.Select(MapToDto).ToList();
    }

    public async Task<JournalEntryDto?> GetByIdAsync(Guid id)
    {
        var entry = await _context.Set<JournalEntry>()
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted);

        return entry is null ? null : MapToDto(entry);
    }

    public async Task<List<JournalEntryDto>> GetByAccountAsync(Guid accountId)
    {
        var entries = await _context.Set<JournalEntry>()
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .Where(e => e.Lines.Any(l => l.AccountId == accountId))
            .OrderByDescending(e => e.EntryDate)
            .ThenByDescending(e => e.CreatedAt)
            .ToListAsync();

        return entries.Select(MapToDto).ToList();
    }

    /// <summary>
    /// تاريخ محاسبي صالح: ليس القيمة الافتراضية (DateTime.MinValue — تُخزَّن -infinity في PostgreSQL)
    /// وداخل النطاق المعقول [1900-01-01 .. 2100-01-01].
    /// مصدر واحد للقاعدة: يستخدمه الحارس المركزي في <see cref="PrepareEntryAsync"/> وخدمات ترحيل
    /// المستندات التي تكتب التاريخ في السند/الحركة (مثل <c>StockWriteOffService</c>).
    /// </summary>
    internal static bool IsValidAccountingDate(DateTime date)
        => date != default(DateTime)
           && date >= new DateTime(1900, 1, 1)
           && date <= new DateTime(2100, 1, 1);

    /// <summary>
    /// يجهّز قيداً محاسبياً متوازناً: يتحقق من التوازن والحسابات المتحركة، يبني القيد وبنوده،
    /// ويحدّث رصيد كل حساب وفق طبيعته، ثم يضيفه إلى السياق دون حفظ (ليحفظه المتصل ذرياً).
    /// </summary>
    public async Task<JournalEntry> PrepareEntryAsync(
        JournalEntryType entryType,
        DateTime entryDate,
        string? reference,
        string? description,
        IReadOnlyCollection<JournalEntryLegInput> legs)
    {
        if (legs is null || legs.Count == 0)
            throw new InvalidOperationException("لا يمكن إنشاء قيد بدون بنود.");

        // حماية التاريخ (جذر -infinity): لا يُقبل أي تاريخ غير محدد (DateTime.MinValue → -infinity في PostgreSQL)
        // ولا تاريخ خارج النطاق المعقول. التحقق مركزي هنا ⇒ يحمي كل القيود اليدوية والتلقائية معاً
        // (فواتير/مردودات/مصروفات/إتلاف/شيكات/ورديات/رواتب) وقبل أي كتابة أو ترقيم.
        if (!IsValidAccountingDate(entryDate))
            throw new InvalidOperationException(
                "تاريخ القيد غير صالح. لا يمكن ترحيل قيد بتاريخ غير محدد أو خارج النطاق المسموح (1900-2100).");

        // قفل الفترة المحاسبية: الفترة المغلقة لا تقبل ترحيلاً جديدًا (لا تعديل لأي قيد قائم).
        // الفحص المركزي في نقطة الاختناق الوحيدة ⇒ يغطي كل المسارات بلا استثناء
        // (فواتير/مردودات/مشتريات/سندات/مصاريف/رواتب/أرصدة افتتاحية/قيود يدوية).
        await AccountingPeriodGuard.EnsurePostingAllowedAsync(_context, entryDate);

        // أهم تحقق محاسبي: توازن القيد
        var totalDebit = legs.Sum(l => l.DebitAmount);
        var totalCredit = legs.Sum(l => l.CreditAmount);
        if (totalDebit != totalCredit)
            throw new InvalidOperationException(
                $"القيد غير متوازن: مجموع المدين ({totalDebit:N2}) ≠ مجموع الدائن ({totalCredit:N2}). لا يمكن ترحيله.");

        // القاعدة المحاسبية المركزية (Central Parent Validation):
        // تُمنع الحركة المباشرة على أي حساب رئيسي (Parent)؛ الحسابات المتحركة (Leaf) فقط تُرحَّل إليها.
        // هذه هي نقطة الاختناق الواحدة التي تمر عبرها كل الترحيلات:
        //   القيد اليدوي + واجهات الـAPI/Application + جميع القيود التلقائية
        //   (المبيعات/المردودات/المشتريات/المصاريف/النقد/الرواتب/الشيكات/الإتلاف... إلخ).
        // لا تمسّ هذه القاعدة أي حركة تاريخية — تُطبَّق على القيود الجديدة فقط عند التجهيز.
        await MovementAccountValidator.EnsureMovementAccountsAsync(
            _context, legs.Select(l => l.AccountId).Distinct().ToList());

        // نسجّل معرفات الحسابات (تحديث الرصيد يتم لاحقاً بـ UPDATE ذرّي لكل رِجل).
        var accountIds = legs.Select(l => l.AccountId).Distinct().ToList();

        var accounts = await _context.Set<Account>()
            .Where(a => accountIds.Contains(a.Id))
            .ToListAsync();

        var accountDict = accounts.ToDictionary(a => a.Id);

        var entry = new JournalEntry
        {
            Id = Guid.NewGuid(),
            EntryNumber = await NextNumberAsync("JE"),
            EntryType = entryType,
            EntryDate = entryDate,
            ReferenceNumber = reference,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var leg in legs)
        {
            if (!accountDict.TryGetValue(leg.AccountId, out var account))
                throw new InvalidOperationException("أحد الحسابات في القيد غير موجود.");

            if (leg.DebitAmount < 0 || leg.CreditAmount < 0)
                throw new InvalidOperationException("لا يمكن أن تكون مبالغ القيد سالبة.");

            var line = new JournalEntryLine
            {
                Id = Guid.NewGuid(),
                JournalEntryId = entry.Id,
                AccountId = leg.AccountId,
                DebitAmount = leg.DebitAmount,
                CreditAmount = leg.CreditAmount,
                Note = leg.Note,
                // إسناد الطرف (اختياري): يُخزَّن على البند للأثر التدقيقي ولحساب «غير المُنسَب»
                // في تقرير المطابقة. مزامنة الرصيد التفصيلي تحدث في مسار الإنشاء المختص
                // (القيد اليدوي/الأرصدة الافتتاحية) ولا تتكرر هنا.
                CustomerId = leg.CustomerId,
                SupplierId = leg.SupplierId,
                CreatedAt = DateTime.UtcNow
            };
            entry.Lines.Add(line);

            // تحديث رصيد الحساب وفق طبيعته (مدين/دائن) — ذرّياً في قاعدة البيانات:
            // القراءة-ثم-الكتابة في الذاكرة تفقد تحديثات القيود المتزامنة على نفس الحساب
            // (وهو جذر فروق الأرصدة المخزَّنة عن دفتر القيود). لذا نُطبّق الزيادة بـ UPDATE ذرّي،
            // ثم نُبقي النسخة المتتبَّعة في الذاكرة متوافقة دون إعادة كتابة العمود عبر EF.
            var delta = account.NormalBalance == NormalBalance.Debit
                ? (leg.DebitAmount - leg.CreditAmount)
                : (leg.CreditAmount - leg.DebitAmount);

            if (delta != 0m)
            {
                // داخل معاملة صريحة: زيادة ذرّية في قاعدة البيانات (آمنة ضد التزامن).
                // بلا معاملة (مسار يُحفظ بـ SaveChanges واحدة): تحديث الذاكرة **مع تعليم العمود
                // كمُعدَّل صراحةً** لضمان كتابته ضمن وحدة العمل نفسها (ذرّي مع المستند).
                //
                // إصلاح D-03-01 (تدقيق 2026-10-20): كان الفرع الثاني يكتفي بزيادة الذاكرة
                // اعتمادًا على تتبّع EF التلقائي. في المسارات التي تُجهّز أكثر من قيد للمستند
                // (مثل مردود المبيعات: قيد العكس + قيد عكس التكلفة) تُحمّل الحسابات مرة أخرى
                // من قاعدة البيانات، فيُبنى على القيمة المخزَّنة لا على القيمة في الذاكرة،
                // وينتهي الأمر بفقد فرق الرصيد (فروق مثبتة بين الرصيد المخزَّن ودفتر القيود).
                // الآن: (1) إذا كانت القيمة المتتبَّعة متأخرة عن القاعدة نُطبّق الفرق المتبقّي
                // ذرّيًا في القاعدة، (2) ثم نعلّم العمود كمُعدَّل ليُكتب صراحةً.
                if (_context.Database.CurrentTransaction is not null && _context.Database.IsRelational())
                {
                    // داخل معاملة صريحة: زيادة ذرّية في قاعدة البيانات (آمنة ضد التزامن)، ثم **مزامنة
                    // النسخة المتتبَّعة مع القيمة التي أعادتها القاعدة** (RETURNING) بدل الاعتماد على
                    // قيمة الذاكرة. إصلاح D-03-01: سابقًا كانت الذاكرة تُترك بقيمة قديمة (الزيادة لم
                    // تُعكس عليها) بينما القاعدة تحدّثت؛ فأي مسار لاحق بلا معاملة (مثل مردود المبيعات)
                    // كان يكتب القيمة القديمة فوق الرصيد الصحيح ⇒ فروق دائمة بين الرصيد المخزَّن والقيود.
                    var stored = await IncrementAccountBalanceAsync(account.Id, delta);
                    account.CurrentBalance = stored.HasValue ? stored.Value : account.CurrentBalance + delta;

                    // فصل النسخة المتتبَّعة بعد التحديث الذرّي: القاعدة هي مرجع الحقيقة، وإبقاء الكيان
                    // متتبَّعًا كان يسمح لـ EF بإرجاع قيمته إلى اللقطة القديمة (IsModified=false يُعيد
                    // القيمة الحالية إلى الأصلية) ثم تُكتب لاحقًا فوق الرصيد الصحيح في مسار بلا معاملة.
                    // الفصل يمنع أي كتابة من الذاكرة ويجعل أي استعلام لاحق يقرأ القيمة الحقيقية من القاعدة.
                    _context.Entry(account).State = EntityState.Detached;
                }
                else
                {
                    // بلا معاملة صريحة: يبقى التجهيز "تحضيريًا" كما كان (لا كتابة في القاعدة قبل
                    // SaveChanges) — لكن يُعلَّم العمود كمُعدَّل صراحةً ليُكتب ضمن وحدة العمل نفسها،
                    // بدل الاعتماد على كشف التغييرات (الذي كان يُسقط الزيادة في بعض المسارات).
                    account.CurrentBalance += delta;
                    var entryState = _context.Entry(account);
                    if (entryState.State == EntityState.Detached)
                        entryState.State = EntityState.Unchanged; // إرفاق بلا كتابة كامل الكيان
                    entryState.Property(a => a.CurrentBalance).IsModified = true;
                }

                // تشخيص اختياري (لا يعمل إلا عند تفعيل AUDIT_JE_TRACE=1) لتتبّع فروق الأرصدة.
                if (Environment.GetEnvironmentVariable("AUDIT_JE_TRACE") == "1")
                {
                    var branch = _context.Database.CurrentTransaction is not null ? "tx" : "mem";
                    Console.WriteLine($"[JE-TRACE] {reference} | acct={account.Code} | delta={delta} | branch={branch} | after={account.CurrentBalance}");
                }
            }
        }

        // تجهيز القيد في السياق (دون SaveChanges) ليكون المتصل حراً في حفظه ذرياً
        _context.Set<JournalEntry>().Add(entry);
        _context.Set<JournalEntryLine>().AddRange(entry.Lines);

        return entry;
    }

    /// <summary>
    /// زيادة رصيد الحساب ذرّياً في قاعدة البيانات (UPDATE ... + delta) بدل القراءة-ثم-الكتابة،
    /// فلا يفقد أي قيد متزامن تحديث غيره، **وتعيد القيمة الجديدة من القاعدة** (RETURNING) لتبقى
    /// نسخة الذاكرة المتتبَّعة مطابقة للحقيقة المخزَّنة تمامًا (إصلاح D-03-01).
    /// لا يفعل شيئاً لمزوّد غير علائقي (InMemory في الاختبارات) وتعيد null.
    /// </summary>
    private async Task<decimal?> IncrementAccountBalanceAsync(Guid accountId, decimal delta)
    {
        if (!_context.Database.IsRelational())
            return null;

        var connection = _context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                "UPDATE \"Accounts\" SET \"CurrentBalance\" = \"CurrentBalance\" + @delta WHERE \"Id\" = @id " +
                "RETURNING \"CurrentBalance\"";

            var deltaParam = command.CreateParameter();
            deltaParam.ParameterName = "delta";
            deltaParam.Value = delta;
            command.Parameters.Add(deltaParam);

            var idParam = command.CreateParameter();
            idParam.ParameterName = "id";
            idParam.Value = accountId;
            command.Parameters.Add(idParam);

            var result = await command.ExecuteScalarAsync();
            if (result is null || result is DBNull)
                return null;

            return Convert.ToDecimal(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (wasClosed && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }
}