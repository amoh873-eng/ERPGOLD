using ERPSystem.Domain.Common;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// Represents a single line item on a sales invoice (بند في فاتورة بيع).
/// Stores the item, sold quantity, unit selling price, and the calculated line total.
/// </summary>
public class SalesInvoiceLine : BaseEntity
{
    /// <summary>
    /// Foreign key to the parent invoice.
    /// </summary>
    public Guid SalesInvoiceId { get; set; }

    /// <summary>
    /// Navigation property to the parent invoice.
    /// </summary>
    public SalesInvoice? SalesInvoice { get; set; }

    /// <summary>
    /// Foreign key to the item being sold.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Navigation property to the item.
    /// </summary>
    public Item? Item { get; set; }

    /// <summary>
    /// Quantity sold (always positive).
    /// </summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Unit selling price at the time of sale.
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Unit cost at the time of sale (used for COGS valuation).
    /// Snapshot from the item so historical cost is preserved even if the item price changes later.
    /// </summary>
    public decimal UnitCost { get; set; }

    /// <summary>
    /// Line total = Quantity × UnitPrice (computed, stored for speed).
    /// </summary>
    public decimal LineTotal { get; set; }

    /// <summary>
    /// نسبة ضريبة المبيعات التي طُبّقت على هذا السطر لحظة إنشاء الفاتورة
    /// (لقطة من نسبة الصنف — تحفظ التاريخ الضريبي حتى لو تغيّرت نسبة الصنف لاحقاً).
    /// </summary>
    public decimal TaxRate { get; set; }

    /// <summary>
    /// مبلغ ضريبة المبيعات لهذا السطر (يُحتسب عند الإنشاء ويُخزَّن للمراجعة والتدقيق).
    /// </summary>
    public decimal TaxAmount { get; set; }
}
