using System.Drawing;
using LabelTransPrint.Models;

namespace LabelTransPrint.Services;

/// <summary>Compatibility calls for applications using the production-field printer interface.</summary>
public static class ProductionLabelPrinterExtensions
{
    public static void Print(this LabelPrinterHelper printer, IEnumerable<LabelElement> elements,
        string originalCode, string newCode, string title, string projectCode, string productName,
        string sideLabel, string partNumber, string revisionCode, string hardwareVersion,
        string softwareVersion, string drawingVersion, string vehicleModelCode, string supplierCode,
        string sequenceCode, string shiftCode, DateTime productionDate)
        => printer.Print(elements, ProductionLabelContext.Create(originalCode, newCode, title, projectCode,
            productName, sideLabel, partNumber, revisionCode, hardwareVersion, softwareVersion, drawingVersion,
            vehicleModelCode, supplierCode, sequenceCode, shiftCode, productionDate).CreateFields());

    public static Bitmap GenerateLabelBitmap(this LabelPrinterHelper printer, IEnumerable<LabelElement> elements,
        string originalCode, string newCode, string title, string projectCode, string productName,
        string sideLabel, string partNumber, string revisionCode, string hardwareVersion,
        string softwareVersion, string drawingVersion, string vehicleModelCode, string supplierCode,
        string sequenceCode, string shiftCode, DateTime productionDate)
        => printer.GenerateLabelBitmap(elements, ProductionLabelContext.Create(originalCode, newCode, title, projectCode,
            productName, sideLabel, partNumber, revisionCode, hardwareVersion, softwareVersion, drawingVersion,
            vehicleModelCode, supplierCode, sequenceCode, shiftCode, productionDate).CreateFields());
}
