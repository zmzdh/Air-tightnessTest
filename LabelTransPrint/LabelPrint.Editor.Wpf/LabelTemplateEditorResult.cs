using LabelTransPrint.Models;

namespace LabelTransPrint;

public sealed record LabelTemplateEditorResult(bool Accepted, LabelTemplate Template);
