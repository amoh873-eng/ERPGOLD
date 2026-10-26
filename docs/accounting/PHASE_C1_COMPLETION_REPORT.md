# Phase C1 — COGS Precision Consistency — Completion Report

- **Date:** 2026-10-10
- **Workspace:** `/home/dev/erp-work`
- **Branch:** `master`
- **HEAD (unchanged):** `cdae2cd409be1ccfc34acb550a0e98a2097c1a59`
- **Status:** C1 verified complete, built and tested. **Nothing committed, deployed, or migrated.**

---

## 1. Summary of existing work preserved

The interrupted Phase C1 working tree was inspected in full and **left byte-for-byte
unchanged** except for the addition of this report. No reset, clean, stash, overwrite,
revert, or file recreation was performed. The migration and the (already-present) new
tests were not rewritten.

The prior status note "the new tests were not written" was **stale**: both the new test
file and the corrected assertions already existed in the working tree from the
interrupted run (timestamps 2026-10-10 03:57–03:59). They had simply not been compiled
or executed. They are correct and now verified.

### Working tree at completion (identical to the interrupted state)

Modified tracked files (9):

| File | Change |
| --- | --- |
| `src/Application/Services/SalesInvoiceService.Create.cs` | COGS uses `MoneyRounding.Round(...)` on the invoice total; comment documents the four-decimal internal snapshot |
| `src/Application/Services/SalesReturnService.Create.cs` | Reversed COGS uses `MoneyRounding.Round(...)` |
| `src/Application/Services/StockWriteOffService.cs` | Write-off total uses `MoneyRounding.Round(...)` |
| `src/Infrastructure/Data/Configurations/SalesInvoiceLineConfiguration.cs` | `UnitCost` → `decimal(18,4)` |
| `src/Infrastructure/Data/Configurations/SalesReturnLineConfiguration.cs` | `UnitCost` → `decimal(18,4)` |
| `src/Infrastructure/Data/Configurations/StockMovementConfiguration.cs` | `UnitCost` → `decimal(18,4)` |
| `src/Infrastructure/Data/Configurations/StockWriteOffConfiguration.cs` | `UnitCost` → `decimal(18,4)` |
| `src/Infrastructure/Migrations/AppDbContextModelSnapshot.cs` | Four `UnitCost` properties → `decimal(18,4)` |
| `tests/ERPSystem.Tests/PricingPrecisionTests.cs` | `SalesInvoiceLine.UnitCost` assertion corrected to `numeric(18,4)` |

Untracked new files (4):

| File | Purpose |
| --- | --- |
| `src/Application/Services/MoneyRounding.cs` | Explicit, named money-rounding policy |
| `src/Infrastructure/Migrations/20261009212922_IncreaseCostSnapshotPrecision.cs` | C1 migration |
| `src/Infrastructure/Migrations/20261009212922_IncreaseCostSnapshotPrecision.Designer.cs` | Migration target model |
| `tests/ERPSystem.Tests/CostPrecisionCogsTests.cs` | C1 precision/COGS tests |

### Files changed by this completion pass

- **Source/tests:** none. No correction was required; the existing implementation and
  tests already satisfy C1 and pass.
- **Added:** `docs/accounting/PHASE_C1_COMPLETION_REPORT.md` (this report only).

---

## 2. Confirmed precision mapping

Internal cost snapshots → `numeric(18,4)` (verified in the Npgsql model, in the new
migration snapshot, in the generated SQL, and in the live PostgreSQL schema):

| Entity.Column | Column type | Role |
| --- | --- | --- |
| `Item.CostPrice` | `numeric(18,4)` | Internal weighted-average cost (already 18,4 from the prior migration) |
| `SalesInvoiceLine.UnitCost` | `numeric(18,4)` | COGS snapshot at sale time |
| `StockMovement.UnitCost` | `numeric(18,4)` | Stock ledger cost snapshot |
| `SalesReturnLine.UnitCost` | `numeric(18,4)` | Copy of the original sale cost snapshot (exact reversal) |
| `StockWriteOff.UnitCost` | `numeric(18,4)` | Write-off cost snapshot |

Preserved at two decimals (money/supplier prices — unchanged by C1):

| Entity.Column | Column type | Role |
| --- | --- | --- |
| `Item.SalePrice` | `numeric(18,2)` | Customer sale price |
| `SalesInvoiceLine.UnitPrice` | `numeric(18,2)` | Customer line price |
| `PurchaseInvoiceLine.UnitCost` | `numeric(18,2)` | **Supplier price** fed into the weighted average — source analysis confirms this is the external supplier price, not an internal cost snapshot, so it is intentionally preserved |
| `PurchaseReturnLine.UnitCost` | `numeric(18,2)` | Supplier price |

### Calculation-path consistency (source review)

- **Sales COGS** — `SalesInvoiceService.Create.cs`: line snapshots `item.CostPrice`
  (four-decimal); `StockMovement.UnitCost` is copied from the same snapshot;
  `cogsTotal = MoneyRounding.Round(lines.Sum(l => l.Quantity * l.UnitCost))` — rounded
  **once** on the invoice total.
- **Sales-return reversal** — `SalesReturnService.Create.cs`: line copies
  `invoiceLine.UnitCost` (four-decimal) so the reversal exactly mirrors the original
  sale; `cogsTotal = MoneyRounding.Round(cogsTotal)`.
- **Stock write-off** — `StockWriteOffService.cs`: `unitCost = item.CostPrice`;
  `total = MoneyRounding.Round(dto.Quantity * unitCost)`; the same `unitCost` is stored
  on `StockWriteOff.UnitCost` and `StockMovement.UnitCost` and posted to the journal.
- **Purchase receipt / weighted average** — `PurchaseInvoiceService.cs` +
  `InventoryValuation.ComputeWeightedAverage` (rounds to 4 decimals) produce
  `Item.CostPrice`; the purchase line keeps its two-decimal supplier price.
- **StockMovementService** inbound movements also feed `InventoryValuation` identically.

---

## 3. Rounding policy and rationale

`src/Application/Services/MoneyRounding.cs` (new) makes the money-rounding policy
**explicit and named**:

- `Scale = 2` (qirsh / two decimals)
- `Mode = MidpointRounding.ToEven` (banker's rounding)
- `Round(value) => Math.Round(value, Scale, Mode)`

Rationale: `Math.Round(value, 2)` **without** a mode already defaults to
`MidpointRounding.ToEven`. The change therefore **does not alter** the established
accounting behaviour; it only removes the reliance on an implicit default so it cannot
be changed accidentally and is documented. No unrelated accounting calculation was
modified.

Exact-midpoint behaviour (documented and tested): `6.225 → 6.22`, `6.235 → 6.24`,
`15.205 → 15.20`, `15.215 → 15.22`, `0.125 → 0.12`, `0.135 → 0.14`.
Midpoint bank rounding differs from SQL/Excel "half away from zero"
(`ROUND(0.125,2)` = `0.13`); this is a deliberate, documented policy choice.

**Unresolved ambiguity (limitation):** PostgreSQL `round(numeric, 2)` uses
half-away-from-zero. Any future *database-side* recomputation of a money total that is
an exact midpoint could differ from the application result by one qirsh. The application
is the system of record for posting; the new tests pin the application behaviour so a
future policy decision would be explicit. Changing this policy is a finance decision and
was not done here.

---

## 4. Migration status and exact database target

**Migration:** `20261009212922_IncreaseCostSnapshotPrecision`
**Model/snapshot check:** `dotnet ef migrations has-pending-model-changes` →
`No changes have been made to the model since the last migration.` (exit 0). The
migration, its `.Designer.cs`, and `AppDbContextModelSnapshot.cs` are consistent.

**Generated SQL (from `AddOpeningBalances` → C1, reviewed, no data loss — widening only):**

```sql
START TRANSACTION;
ALTER TABLE "StockWriteOffs"    ALTER COLUMN "UnitCost" TYPE numeric(18,4);
ALTER TABLE "StockMovements"    ALTER COLUMN "UnitCost" TYPE numeric(18,4);
ALTER TABLE "SalesReturnLines"  ALTER COLUMN "UnitCost" TYPE numeric(18,4);
ALTER TABLE "SalesInvoiceLines" ALTER COLUMN "UnitCost" TYPE numeric(18,4);
INSERT INTO "__EFMigrationsHistory" ("MigrationId","ProductVersion")
VALUES ('20261009212922_IncreaseCostSnapshotPrecision','10.0.11');
COMMIT;
```

### Target database verification (no credentials shown)

- **Candidate isolated test DB (confirmed):** Docker container `<TEST-DB-CONTAINER>`
  (`postgres:16-alpine`), reachable at `127.0.0.1:55432`, database `<TEST_DB>`,
  user `erp_app` (password read at runtime from the container and never printed).
- Its `__EFMigrationsHistory` contains **13 migrations**, latest
  `20261007185533_IncreaseItemCostPricePrecision`.
- The EF migration list contains **15 migrations**. Pending on the target:
  1. `20261009161900_AddOpeningBalances` — **unrelated, pre-existing pending migration**
  2. `20261009212922_IncreaseCostSnapshotPrecision` — C1

### Decision: **migration NOT applied** (instructed blocker)

Applying `dotnet ef database update` to `<TEST-DB-CONTAINER>` would also run the unrelated
pending `AddOpeningBalances` migration. Per the Phase C1 rules ("*unrelated migrations
would run → do not apply anything*"), **no migration was applied and no backup was
taken** (no apply was attempted, so a pre-apply backup was unnecessary). This is the
exact, deliberate blocker.

**Safe validation performed instead:** the repository's PostgreSQL integration fixture
(`AccountingPgFixture`) creates a throwaway `erp_a5_*` database on the same isolated
server, applies **all** migrations (including C1), and drops it. Via this path the C1
migration was applied to ephemeral databases and the live schema was verified to be
`numeric(18,4)` for the four internal columns. After the run:
- leftover `erp_a5_*` databases: **0**
- `<TEST-DB-CONTAINER>` / `<TEST_DB>` history: **still 13 migrations (unchanged)** — nothing was
  applied to the persistent candidate database.

**Databases never touched:** `<PRODUCTION-DB>`, `<PRODUCTION-DB>`, the `<CUSTOMER-DB-*>` databases,
`<POS-DB>`, `<OTHER-DB>`, and no migration was applied to `<TEST-DB-CONTAINER>`'s
persistent `<TEST_DB>`. `/opt/erp` and `<CUSTOMER-DEPLOYMENT-PATH>` were not modified.
No service was restarted; no image was rebuilt.

---

## 5. Build and test evidence

Toolchain: .NET SDK `10.0.401`, PostgreSQL client `16.15`, `dotnet-ef` `10.0.11`.

### Builds (all relevant projects)

| Command | Result | Exit |
| --- | --- | --- |
| `dotnet build src/Domain/ERPSystem.Domain.csproj` | Build succeeded, 0 warnings | 0 |
| `dotnet build src/Application/ERPSystem.Application.csproj` | Build succeeded, 0 warnings | 0 |
| `dotnet build src/Infrastructure/ERPSystem.Infrastructure.csproj` | Build succeeded, 0 warnings | 0 |
| `dotnet build src/Web/ERPSystem.Web.csproj` | Build succeeded | 0 |
| `dotnet build src/Api/ERPSystem.Api.csproj` | Build succeeded (pre-existing nullable CS8604/CS8321 warnings in `Program.cs`, unrelated to C1) | 0 |
| `dotnet build tests/ERPSystem.Tests/ERPSystem.Tests.csproj` | Build succeeded | 0 |

`dotnet build ERPSystem.slnx` (whole solution) fails **only** because the MAUI Mobile
project requires the `maui-android` workload (`NETSDK1147`), which is not installed in
this environment. This is unrelated to C1 and does not affect any C1 project.

### Tests (repository PostgreSQL setup)

Command (connection details read at runtime, password never printed):

```
ERPSYSTEM_TEST_PG="Host=127.0.0.1;Port=55432;Database=<TEST_DB>;Username=erp_app;Password=<from container>"
dotnet test tests/ERPSystem.Tests/ERPSystem.Tests.csproj -c Debug
```

Result:

```
Test Run Successful.
Total tests: 143
     Passed: 143
  Total time: 20.9487 Seconds
TEST_EXIT=0
```

0 failed, 0 skipped — the live PostgreSQL tests executed (not skipped) because
`ERPSYSTEM_TEST_PG` was set.

**C1 tests executed (all passed):**

- `Cost_snapshot_columns_map_to_numeric_18_4_in_the_npgsql_model`
- `Cost_snapshot_columns_are_numeric_18_4_in_postgresql`
- `MoneyRounding_is_two_decimals_banker_rounding_and_handles_exact_midpoints`
- `Sale_snapshots_the_four_decimal_cost_and_posts_matching_cogs` (10.1320 × 3 → 30.40)
- `Sale_at_an_exact_midpoint_cost_rounds_to_even_in_posted_cogs` (10.125 → 10.12)
- `Cogs_is_rounded_once_on_the_invoice_total_not_per_line` (3 × 10.1250 → 30.38, not 30.36)
- `Return_reverses_cogs_and_write_off_posts_from_the_four_decimal_snapshot`
  (reversal 10.13; write-off 2 × 10.1320 → 20.26)
- `Sale_price_tax_and_discount_are_unaffected_by_the_cost_precision_change`
  (regression: line price/tax/discount/COGS/sale journal unchanged)

Coverage of the required categories: (1) `numeric(18,4)` mappings ✔
(model + live), (2) `10.1320` preserved through snapshots ✔, (3) quantity × stored
four-decimal cost ✔, (4) exact-midpoint rounding boundaries ✔, (5) sales COGS + journal
totals ✔, (6) sales-return reversal + write-off ✔, (7) price/tax/discount/accounting
regression ✔.

---

## 6. Remaining concerns and limitations

1. **Migration not applied to the persistent test DB** — blocked deliberately by the
   unrelated pending `20261009161900_AddOpeningBalances`. When the owner decides to
   advance `<TEST-DB-CONTAINER>`, apply migrations through the normal release process (which will
   include `AddOpeningBalances` first), after a verified backup. C1 itself is validated
   via ephemeral migrated databases.
2. **Midpoint rounding vs SQL** — application uses `ToEven`; SQL uses away-from-zero.
   Application-side posting is authoritative; documented in `MoneyRounding.cs` and
   pinned by tests. Any change is a finance-policy decision, not made here.
3. **Whole-solution build** — fails only on the MAUI Mobile workload, unrelated to C1.
4. **Purchase line cost** — `PurchaseInvoiceLine/PurchaseReturnLine.UnitCost` remain two
   decimals by design (supplier prices). If a future requirement treats a purchase line
   cost as an internal snapshot, this mapping must be revisited.

---

## 7. Git status and current HEAD

```
HEAD:   cdae2cd409be1ccfc34acb550a0e98a2097c1a59
Branch: master
```

```
 M src/Application/Services/SalesInvoiceService.Create.cs
 M src/Application/Services/SalesReturnService.Create.cs
 M src/Application/Services/StockWriteOffService.cs
 M src/Infrastructure/Data/Configurations/SalesInvoiceLineConfiguration.cs
 M src/Infrastructure/Data/Configurations/SalesReturnLineConfiguration.cs
 M src/Infrastructure/Data/Configurations/StockMovementConfiguration.cs
 M src/Infrastructure/Data/Configurations/StockWriteOffConfiguration.cs
 M src/Infrastructure/Migrations/AppDbContextModelSnapshot.cs
 M tests/ERPSystem.Tests/PricingPrecisionTests.cs
?? src/Application/Services/MoneyRounding.cs
?? src/Infrastructure/Migrations/20261009212922_IncreaseCostSnapshotPrecision.Designer.cs
?? src/Infrastructure/Migrations/20261009212922_IncreaseCostSnapshotPrecision.cs
?? tests/ERPSystem.Tests/CostPrecisionCogsTests.cs
?? docs/accounting/PHASE_C1_COMPLETION_REPORT.md
```

**Not committed. Not deployed. Awaiting review and explicit approval before any further
phase.**
