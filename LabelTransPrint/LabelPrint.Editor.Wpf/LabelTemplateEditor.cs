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
            context.ProductTitle,
            context.PartNumber,
            context.BackupField1,
            context.BackupField2,
            context.SequenceCode,
            context.ProductionDate,
            context.OperatorName,
            context.FieldValues,
            context.Fields)
        {
            Owner = owner
        };

        var accepted = editor.ShowDialog() == true;
        return new LabelTemplateEditorResult(accepted, accepted ? editor.EditedTemplate : template);
    }
}
