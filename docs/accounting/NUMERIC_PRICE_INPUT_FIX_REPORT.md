# Numeric Price Input (keyboard / keypad) — Investigation & Fix Report

- **Date:** 2026-10-10
- **Workspace:** `<SOURCE-REPO>`
- **Git:** branch `master`, HEAD `d1e28c2383dd2921352afacc105d8e5335573fe3` (clean working tree)
- **Reported live page:** `<CUSTOMER-URL>/sales/invoices` (commercial deployment, port 8082)

## Result in one line

The reported defect is **real on the live deployment**, but the **working copy of the source
already contains the fix** for all three screens. The live service at `:8082` runs an image
built **2026-09-28**, which **predates** the fix commit **2026-10-08**. Therefore **no source
change is required**; the correct remedy is to **rebuild and redeploy**, which this task
prohibits. This is stated here explicitly as required.

---

## Phase 1 — Findings

### 1.1 Shared component? (confirmed)

The three screens do **not** use a shared custom numeric-input component. Each renders
MudBlazor's `MudNumericField<T>` directly and applies the project's established convention
of setting the field's `Culture` to `InvariantCulture`:

| Screen | File | Price field(s) |
| --- | --- | --- |
| Sales invoice | `src/Web/Components/Pages/Sales/SalesInvoiceFormDialog.razor` | `line.UnitPrice` (line 128) |
| Items | `src/Web/Components/Pages/Inventory/ItemFormDialog.razor` | `_costPrice` (line 67), `_salePrice` (line 77) |
| Purchase invoice | `src/Web/Components/Pages/Purchases/PurchaseInvoiceFormDialog.razor` | `line.UnitCost` (line 126) |

A per-file audit shows **every** `MudNumericField` in the three dialogs carries
`Culture="@CultureInfo.InvariantCulture"` (Sales 3/3, Items 5/5, Purchase 2/2). No competing
parsing system exists in these screens (no `decimal.Parse`/`NumberStyles` there). The decimal
helpers that do exist are confined to POS/Finance/Shift pages (see §1.5).

### 1.2 Root cause (confirmed)

1. **App culture is Arabic by default.** `src/Web/Program.cs`:
   - line 46: `var supportedCultures = new[] { "ar", "en" };`
   - line 48: `.SetDefaultCulture(supportedCultures[0])` → default = `ar`
   - lines 54-55: only `CookieRequestCultureProvider` is used, so the browser's
     `Accept-Language` is ignored and the UI defaults to Arabic.
2. **MudNumericField's default converter uses `CurrentUICulture`.** `MudNumericField<T>`
   derives from `MudBaseInput<T>`, which constructs `new DefaultConverter<T>()`; that
   converter's default `Culture` is `CultureInfo.CurrentUICulture`.
3. **Arabic uses a non-Latin decimal separator.** Under `ar`, the decimal separator is
   `ُU+066B` ("٫") and digits are rendered as Arabic-Indic. The Latin input `"2.38"` therefore
   fails to parse and the bound value becomes `0` — exactly the reported symptom.

This mechanism is pinned directly by the repo's own test
`tests/ERPSystem.Tests/LatinDecimalInputTests.cs`
(`Arabic_ui_culture_rejects_latin_dot_input` asserts `converter.Get("2.38") == 0m`).

**The established fix** (commit `48d49d4 fix: use latin digits for decimal price input`,
2026-10-08) sets `Culture="@CultureInfo.InvariantCulture"` on the numeric fields so parsing
uses Latin digits and the `.` decimal separator, **without a global culture change**.

### 1.3 Sales invoice screen (confirmed)

- `line.UnitPrice` is bound two-way (`@bind-Value`) with `Culture=InvariantCulture`,
  `DecimalPlaces="2"`, `Format="0.00"`.
- On item selection, `OnLineItemChanged` sets `line.UnitPrice = item.SalePrice` (default kept
  when a line is first selected). Subsequent manual edits on the line are **not** overwritten,
  because the handler only runs on item change.
- `ReadOnly="@(!_canEditPrice)"` preserves the existing **`Sales.Edit`** authorization for
  manual price editing; the master item's `SalePrice` is never written by the invoice.
- Save path sends `CreateSalesInvoiceLineDto.UnitPrice`; no code resets it to zero.

### 1.4 Items screen and Purchase screen (confirmed)

- Items: `CostPrice` uses `DecimalPlaces="4" Format="0.0000"` (four-decimal internal precision,
  matching `Item.CostPrice numeric(18,4)`), `SalePrice` uses `0.00` (two-decimal money). Both
  carry `Culture=InvariantCulture`; `_costPrice`/`_salePrice` are loaded from `EditItem` and
  sent on create/update. `ReadOnly` is gated by **`Inventory.Edit`**.
- Purchase: `line.UnitCost` (supplier purchase price) carries `Culture=InvariantCulture`,
  defaults from `item.CostPrice`, and is what feeds `CreatePurchaseInvoiceLineDto.UnitCost`.
  This remains the **two-decimal supplier price** (`PurchaseInvoiceLine.UnitCost numeric(18,2)`);
  it is not the four-decimal internal inventory cost snapshot, and no valuation/tax/accounting
  logic was touched.

### 1.5 Deployment evidence (confirmed — live does NOT contain the fix)

- Live `:8082` is the commercial deployment: container `<PRODUCTION-WEB>` running image
  `<PRODUCTION-IMAGE>:latest`, image id `<IMAGE-DIGEST>`,
  **created `2026-09-28T17:58:59Z`** (`ASPNETCORE_ENVIRONMENT=Production`).
- The Latin-decimal fix `48d49d4` was committed **`2026-10-08`**, and the permission fix
  `60f8578` on **`2026-10-08`** — both **~10 days after** the deployed image was built.
- `48d49d4` and `60f8578` are confirmed ancestors of HEAD.
- Conclusion: the live build was produced **before** the fix existed, so it cannot contain it.
  (The `<WEB-IMAGE>`/`<API-IMAGE>` images show the same 2026-09-28 build date, so all current
  deployments are pre-fix.)

### 1.6 Why the fix is effective (mechanism verified)

Upstream MudBlazor `v8.15.0` `MudNumericField.razor.cs` registers a real `Culture` parameter
(`ParameterState<CultureInfo>(nameof(Culture))`); setting it drives the field's
`DefaultConverter<T>.Culture`, which is what parses/formats the text. The repo's
`LatinDecimalInputTests` verifies the converter behaviour directly (`"2.38" → 2.38`, formatting
`0.00`). Hence the existing per-field `Culture=InvariantCulture` is the correct, non-global fix.

---

## Phase 2 — Correcting the affected screens

**No source edits were made.** Every requirement in Phase 2 (A/B/C/D) is already satisfied by
the working copy at HEAD; adding changes would duplicate the established fix and risk unrelated
regressions. Requirement-by-requirement evidence:

**A. Sales invoices** — `line.UnitPrice` is `MudNumericField` with
`Culture=InvariantCulture`, `DecimalPlaces=2`, `Format=0.00`; default kept from
`item.SalePrice` on item selection (`OnLineItemChanged`, line 271); manual edits persist
through rerenders/Tab/save (`@bind-Value`); `ReadOnly` gated by `Sales.Edit`
(`ManualPriceRules.IsAuthorized(..., ManualPriceModules.Sales)`); the master item price is
never written by the invoice; backend authorization unchanged.
**B. Items** — `_costPrice` (4 dp, `0.0000`) and `_salePrice` (2 dp, `0.00`) are
`MudNumericField` with `Culture=InvariantCulture`; loaded from `EditItem`; sent on
create/update; `ReadOnly` gated by `Inventory.Edit`.
**C. Purchase invoices** — `line.UnitCost` with `Culture=InvariantCulture`, defaults from
`item.CostPrice`, sent as `CreatePurchaseInvoiceLineDto.UnitCost`; supplier price kept as
`numeric(18,2)`; no change to weighted-average valuation, taxes, discounts, or journal entries.
**D. Consistency** — the same convention (`Culture=InvariantCulture` on `MudNumericField`) is
used across all three screens; ordinary integers (`12`, `125`) and decimals (`2.38`, `10.50`,
`125.99`) parse identically for the main keyboard and the numeric keypad (both emit the same
characters). No global culture change and no duplicate parsing infrastructure were introduced.

> Scope note (per instructions): other dialogs and the POS screen still lack the `Culture`
> convention (e.g. `Settings.razor`, `ExpenseFormDialog.razor`, `SupplierFormDialog.razor`,
> `CustomerFormDialog.razor`, `PayrollRunDetailsDialog.razor`, `BarcodeLabels.razor`,
> `SlowMovingStock.razor`). These are **outside the three reported areas** and were **not**
> modified. POS does **not** use `MudNumericField` for its quantity input (it uses a raw
> `<input type="number">` plus its own `decimal.TryParse` helpers), so it is **not** the same
> defective component and was left untouched.

## Phase 3 — Targeted verification

Commands and **actual** results:

| Command | Result |
| --- | --- |
| `dotnet test tests/ERPSystem.Tests/ERPSystem.Tests.csproj -c Debug --filter "FullyQualifiedName~LatinDecimalInputTests"` | **Passed! Failed: 0, Passed: 6, Skipped: 0, Total: 6** — exit 0 |
| `dotnet build src/Web/ERPSystem.Web.csproj -c Debug --no-restore` | **Build succeeded, 0 errors**, 4 pre-existing warnings (unrelated: `Checks.razor`, `GeneralLedger.razor`), exit 0 |

Interpretation:
- The Latin-decimal converter tests confirm the mechanism: under `ar` the default converter
  rejects `"2.38"` (→ 0), and with `InvariantCulture` it parses/formats `2.38`, `1.75`,
  `10.50`, `125.99` correctly. This is the exact behaviour the three screens now use.
- The Web build confirms the three fixed dialogs compile.

**Not verified (and explicitly not claimed):**
- The **live page at `:8082`** was **not** exercised — no browser automation was run (limited
  RAM; and the running container is a stale pre-fix image). A source-code fix does **not**
  establish that the live page is fixed; here the live page genuinely is **not** fixed yet.
- Saving/reopening against a live server was **not** performed (no deploy/DB changes allowed).
  The save path was verified by source review: items send `_costPrice`/`_salePrice`; sales send
  `line.UnitPrice`; purchases send `line.UnitCost`; each is then persisted by the unchanged
  application services.
- Per-field intermediate behaviours (Tab, delete-and-re-enter, change-another-field) were
  reasoned from `MudNumericField`/`@bind-Value` semantics, not executed in a browser.

---

## Files changed

- **Source code:** none. The reporting bug is caused by a **stale deployment**, not by the
  current source.
- **Added (this task):** `docs/accounting/NUMERIC_PRICE_INPUT_FIX_REPORT.md` (this report).

The committed Phase C1 work and the rest of the working tree are untouched. Nothing was
committed, deployed, migrated, or restarted.

## Is the deployed version confirmed to contain the fix?

**No — it is confirmed NOT to contain it.** The `:8082` commercial image
(`sha256:444be9a4…`) was built `2026-09-28T17:58:59Z`; the fix commits are dated
`2026-10-08`. The deployed build therefore predates the fix by ~10 days.

## Remaining issues and limitations

1. **Live page remains broken until redeployed.** Fixing this requires a normal
   rebuild + redeploy of the Web image (and the other deployments, which share the same
   2026-09-28 build date). Deployment is prohibited in this task, so it was not done.
2. **Other screens still lack the `Culture=InvariantCulture` convention** (listed above).
   They are outside the three reported areas and were intentionally left unchanged; they may
   exhibit the same symptom and could be addressed in a separate, explicitly-scoped change.
3. **POS is a separate input path** (raw `type="number"` + custom parse helpers) and was not
   touched; no evidence it shares the reported defect.
4. **No live/end-to-end verification** was possible in this environment.

## Recommended next step (not performed)

Rebuild the Web image from the current source and redeploy to `:8082` (and other
deployments), then re-test the three screens manually. That is the only action that fixes the
users' live symptom; no further source change is needed for the three reported areas.

