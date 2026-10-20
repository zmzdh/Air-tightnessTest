using LabelTransPrint.Models;
using LabelTransPrint.Services;
using System.Windows;

namespace LabelTransPrint;

public static class LabelTemplateEditor
{
    public static LabelTemplateEditorResult Edit(
        Window? owner,
        LabelTemplate template,
        LabelTemplateService templateService,
        LabelPrintContext context)
    {
        var editor = new LabelTemplateEditorWindow(
            template,
            templateService,
            context.OriginalCode,
            context.NewCode,
            context.Title,
            context.ProjectCode,
            context.ProductName,
            context.SideLabel,
            context.PartNumber,
            context.RevisionCode,
            context.HardwareVersion,
            context.SoftwareVersion,
            context.DrawingVersion,
            context.VehicleModelCode,
            context.SupplierCode,
            context.SequenceCode,
            context.ShiftCode,
            context.ProductionDate);

        if (owner is not null)
            editor.Owner = owner;

        var accepted = editor.ShowDialog() == true;
        return new LabelTemplateEditorResult(accepted, accepted ? editor.EditedTemplate : template);
    }
}
