using System;
using System.Collections.Generic;
using System.Globalization;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Validation;

public sealed class InvoiceValidationService : IInvoiceValidationService
{
    /// <summary>
    /// Half a minor unit. Paper invoices routinely round each line to 2 decimals, so
    /// demanding exact equality would flag correct invoices as broken.
    /// </summary>
    public const decimal LineTolerance = 0.01m;

    /// <summary>
    /// Looser than the per-line tolerance because rounding error accumulates across
    /// lines: 10 lines each rounded by 0.005 can legitimately drift by 0.05.
    /// </summary>
    public const decimal TotalTolerance = 0.05m;

    public bool IsLineArithmeticValid(InvoiceItem item) =>
        Math.Abs(item.ExpectedTotal - item.TotalPrice) <= LineTolerance;

    public ValidationResult Validate(Invoice invoice, bool requireCatalogLinks = false)
    {
        var issues = new List<ValidationIssue>();

        var counterparty = Counterparty(invoice.InvoiceType);

        if (string.IsNullOrWhiteSpace(invoice.MerchantName))
        {
            issues.Add(new ValidationIssue(
                ValidationCodes.MissingMerchant,
                $"اسم {counterparty} مطلوب.",
                ValidationSeverity.Error,
                FieldName: nameof(Invoice.MerchantName)));
        }
        else if (requireCatalogLinks && invoice.CustomerId is null)
        {
            issues.Add(new ValidationIssue(
                ValidationCodes.UnlinkedCustomer,
                $"يجب اختيار {counterparty} من السجلّات المحفوظة.",
                ValidationSeverity.Error,
                FieldName: nameof(Invoice.CustomerId)));
        }

        if (invoice.InvoiceDate is null)
        {
            issues.Add(new ValidationIssue(
                ValidationCodes.MissingDate,
                "تاريخ الفاتورة مفقود.",
                ValidationSeverity.Warning,
                FieldName: nameof(Invoice.InvoiceDate)));
        }
        else if (invoice.InvoiceDate.Value > DateOnly.FromDateTime(DateTime.Today))
        {
            issues.Add(new ValidationIssue(
                ValidationCodes.FutureDate,
                $"تاريخ الفاتورة {invoice.InvoiceDate:yyyy-MM-dd} في المستقبل.",
                ValidationSeverity.Warning,
                FieldName: nameof(Invoice.InvoiceDate)));
        }

        if (invoice.Items.Count == 0)
        {
            issues.Add(new ValidationIssue(
                ValidationCodes.NoLineItems,
                "لا تحتوي هذه الفاتورة على أي بنود.",
                ValidationSeverity.Warning));
        }

        for (var i = 0; i < invoice.Items.Count; i++)
        {
            var item = invoice.Items[i];
            var rowLabel = $"الصف {i + 1}";

            if (string.IsNullOrWhiteSpace(item.ProductName))
            {
                issues.Add(new ValidationIssue(
                    ValidationCodes.MissingProductName,
                    $"{rowLabel}: اسم المنتج مطلوب.",
                    ValidationSeverity.Error, i, nameof(InvoiceItem.ProductName)));
            }
            else if (requireCatalogLinks && item.ProductId is null)
            {
                issues.Add(new ValidationIssue(
                    ValidationCodes.UnlinkedProduct,
                    $"{rowLabel}: '{item.ProductName}' غير مسجّل في المنتجات. " +
                    "اختر منتجًا من القائمة أو أضِفه إلى المنتجات أولًا.",
                    ValidationSeverity.Error, i, nameof(InvoiceItem.ProductId)));
            }

            if (item.Quantity < 0)
            {
                issues.Add(new ValidationIssue(
                    ValidationCodes.NegativeQuantity,
                    $"{rowLabel}: لا يمكن أن تكون الكمية سالبة.",
                    ValidationSeverity.Error, i, nameof(InvoiceItem.Quantity)));
            }

            if (item.UnitPrice < 0)
            {
                issues.Add(new ValidationIssue(
                    ValidationCodes.NegativeUnitPrice,
                    $"{rowLabel}: لا يمكن أن يكون سعر الوحدة سالبًا.",
                    ValidationSeverity.Error, i, nameof(InvoiceItem.UnitPrice)));
            }

            if (!IsLineArithmeticValid(item))
            {
                issues.Add(new ValidationIssue(
                    ValidationCodes.LineArithmeticMismatch,
                    $"{rowLabel}: {Money(item.Quantity)} × {Money(item.UnitPrice)} = " +
                    $"{Money(item.ExpectedTotal)}، لكن إجمالي البند هو {Money(item.TotalPrice)}.",
                    ValidationSeverity.Error, i, nameof(InvoiceItem.TotalPrice)));
            }
        }

        // Only meaningful when there are lines to sum; an empty invoice is already reported.
        if (invoice.Items.Count > 0 &&
            Math.Abs(invoice.ComputedTotal - invoice.TotalAmount) > TotalTolerance)
        {
            issues.Add(new ValidationIssue(
                ValidationCodes.TotalMismatch,
                $"مجموع البنود هو {Money(invoice.ComputedTotal)}، لكن إجمالي الفاتورة " +
                $"هو {Money(invoice.TotalAmount)}.",
                ValidationSeverity.Warning,
                FieldName: nameof(Invoice.TotalAmount)));
        }

        return new ValidationResult(issues);
    }

    /// <summary>
    /// What the other party is called for this kind of invoice: a sale is made to a
    /// customer, a purchase from a supplier. Both are rows of the same Customers table —
    /// only the word changes.
    /// </summary>
    public static string Counterparty(InvoiceType type) =>
        type == InvoiceType.Sale ? "العميل" : "المورد";

    private static string Money(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
