using LabelTransPrint.Models;
using LabelTransPrint.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LabelTransPrint;

public partial class LabelTemplateEditorWindow : Window
{
    private const double CanvasScale = 7.0;
    private const double BarcodeTextHeightRatio = 0.25;
    private const string DefaultFontFamily = "Microsoft YaHei UI";
    private readonly LabelTemplateService _templateService;
    private readonly ObservableCollection<LabelElement> _items = new();
    private readonly Stack<EditorState> _undoStack = new();
    private readonly Stack<EditorState> _redoStack = new();
    private readonly Dictionary<string, BitmapImage> _qrPreviewCache = new();
    private readonly Dictionary<string, BitmapImage> _barcodePreviewCache = new();
    private readonly Dictionary<string, BitmapImage> _imagePreviewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _title;
    private readonly string _partNumber;
    private readonly string _backupField1;
    private readonly string _backupField2;
    private readonly string _sequenceCode;
    private readonly DateTime _productionDate;
    private readonly string _operatorName;
    private readonly LabelFieldValues? _fieldValues;
    private LabelTemplate _template;
    private LabelElement? _selectedItem;
    private bool _isLoadingControls;
    private bool _suppressSelectionRedraw;
    private bool _isDragging;
    private bool _isResizingTableSegment;
    private bool _isResizingColumn;
    private int _resizingSegmentIndex;
    private bool? _selectedTableLineIsColumn;
    private int _selectedTableLineIndex = -1;
    private int _selectedTableLineSegmentIndex = -1;
    private List<LabelElement> _clipboardItems = new();
    private bool _isApplyingHistory;
    private bool _propertyEditSnapshotCaptured;
    private bool _dragSnapshotCaptured;
    private bool _tableResizeSnapshotCaptured;
    private Point _dragStart;
    private double _dragStartX;
    private double _dragStartY;

    public LabelTemplateEditorWindow(
        LabelTemplate template,
        LabelTemplateService templateService,
        string title,
        string partNumber,
        string backupField1,
        string backupField2,
        string sequenceCode,
        DateTime productionDate,
        string operatorName,
        LabelFieldValues? fieldValues = null,
        IReadOnlyList<LabelTemplateField>? fields = null)
    {
        InitializeComponent();
        _templateService = templateService;
        _template = _templateService.Clone(template);
        _title = title;
        _partNumber = partNumber;
        _backupField1 = backupField1;
        _backupField2 = backupField2;
        _sequenceCode = sequenceCode;
        _productionDate = productionDate;
        _operatorName = operatorName;
        _fieldValues = fieldValues;

        FieldComboBox.ItemsSource = CreateGroupedFieldView(fields ?? _templateService.Fields);
        FontFamilyComboBox.ItemsSource = GetSystemFontNames();
        ItemsListBox.ItemsSource = _items;
        PreviewKeyDown += LabelTemplateEditorWindow_PreviewKeyDown;
        AttachPropertyEditHandlers();
        DesignCanvas.MouseMove += DesignCanvas_MouseMove;
        DesignCanvas.MouseLeftButtonUp += DesignCanvas_MouseLeftButtonUp;
        LoadTemplateToControls();
    }

    public LabelTemplate EditedTemplate => _template;

    private static ICollectionView CreateGroupedFieldView(IEnumerable<LabelTemplateField> fields)
    {
        var view = CollectionViewSource.GetDefaultView(fields.ToArray());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LabelTemplateField.Category)));
        return view;
    }

    private void AttachPropertyEditHandlers()
    {
        TextBoxBase[] textBoxes =
        [
            ContentTextBox,
            XTextBox,
            YTextBox,
            WidthTextBox,
            HeightTextBox,
            FontSizeTextBox,
            LineThicknessTextBox,
            RowCountTextBox,
            ColumnCountTextBox
        ];

        foreach (var textBox in textBoxes)
        {
            textBox.GotKeyboardFocus += (_, _) => _propertyEditSnapshotCaptured = false;
            textBox.LostKeyboardFocus += (_, _) => _propertyEditSnapshotCaptured = false;
        }
    }

    private void LoadTemplateToControls(IReadOnlyList<int>? selectedIndices = null)
    {
        _isLoadingControls = true;
        TemplateCodeTextBox.Text = _template.TemplateCode;
        PaperWidthTextBox.Text = Format(_template.LabelWidthMm);
        PaperHeightTextBox.Text = Format(_template.LabelHeightMm);
        RemoveStackedDuplicateCodeElements(_template.Elements);
        _items.Clear();
        foreach (var item in _template.Elements)
            _items.Add(item);

        RestoreSelection(selectedIndices);
        _isLoadingControls = false;
        LoadSelectedItemToControls();
        RedrawCanvas();
        RefreshPreview();
    }

    private void RestoreSelection(IReadOnlyList<int>? selectedIndices)
    {
        var indices = selectedIndices?.Where(x => x >= 0 && x < _items.Count).Distinct().ToArray();
        if (indices is { Length: > 0 })
        {
            _selectedItem = _items[indices[^1]];
            ItemsListBox.SelectedIndex = indices[^1];
            return;
        }

        ItemsListBox.SelectedIndex = _items.Count > 0 ? 0 : -1;
        _selectedItem = ItemsListBox.SelectedItem as LabelElement;
    }

    private void RedrawCanvas()
    {
        ApplyPaperControls();
        DesignCanvas.Width = Math.Max(1, _template.LabelWidthMm * CanvasScale);
        DesignCanvas.Height = Math.Max(1, _template.LabelHeightMm * CanvasScale);
        DesignCanvas.Children.Clear();

        foreach (var item in _items)
        {
            var element = CreateElement(item);
            Canvas.SetLeft(element, item.X * CanvasScale);
            Canvas.SetTop(element, item.Y * CanvasScale);
            DesignCanvas.Children.Add(element);
        }
    }

    private FrameworkElement CreateElement(LabelElement item)
    {
        FrameworkElement content = item.Type switch
        {
            LabelElementType.QrCode => CreateQrElement(item),
            LabelElementType.Barcode => CreateBarcodeElement(item),
            LabelElementType.Image => CreateImageElement(item),
            LabelElementType.Line => CreateLineElement(item),
            LabelElementType.Table => CreateTableElement(item),
            _ => new TextBlock
            {
                Text = ResolveElementContent(item),
                FontFamily = new FontFamily(ResolveElementFontFamilyName(item)),
                FontSize = CreatePreviewFontSize(item),
                FontWeight = item.IsBold ? FontWeights.Bold : FontWeights.Normal,
                TextAlignment = item.HorizontalAlignment?.Trim().ToLowerInvariant() switch
                {
                    "center" => TextAlignment.Center,
                    "right" => TextAlignment.Right,
                    _ => TextAlignment.Left
                },
                TextWrapping = TextWrapping.Wrap
            }
        };

        var isSelected = ReferenceEquals(item, _selectedItem);
        var border = new Border
        {
            Width = Math.Max(1, item.Width * CanvasScale),
            Height = Math.Max(1, item.Height * CanvasScale),
            BorderBrush = isSelected ? Brushes.DodgerBlue : Brushes.Gray,
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            Background = item.Type is LabelElementType.QrCode or LabelElementType.Barcode or LabelElementType.Image ? Brushes.White : Brushes.Transparent,
            Child = content,
            Tag = item,
            Cursor = Cursors.SizeAll
        };

        border.MouseLeftButtonDown += Item_MouseLeftButtonDown;
        border.MouseMove += Item_MouseMove;
        border.MouseLeftButtonUp += Item_MouseLeftButtonUp;
        return border;
    }

    private int[] GetSelectedIndices()
        => _selectedItem is null ? [] : [_items.IndexOf(_selectedItem)];

    private EditorState CaptureEditorState()
    {
        var templateCode = LabelTemplateService.NormalizeTemplateCode(TemplateCodeTextBox.Text);
        return new EditorState(
            new LabelTemplate
            {
                TemplateCode = templateCode,
                DisplayName = templateCode,
                LabelWidthMm = TryParse(PaperWidthTextBox.Text, out var width) ? Math.Max(1, width) : _template.LabelWidthMm,
                LabelHeightMm = TryParse(PaperHeightTextBox.Text, out var height) ? Math.Max(1, height) : _template.LabelHeightMm,
                Elements = _items.Select(CloneElement).ToList()
            },
            GetSelectedIndices());
    }

    private LabelElement CloneElement(LabelElement element)
    {
        var template = _templateService.Clone(new LabelTemplate { Elements = [element] });
        return template.Elements.First();
    }

    private void PushUndoState()
    {
        if (_isLoadingControls || _isApplyingHistory)
            return;

        _undoStack.Push(CaptureEditorState());
        _redoStack.Clear();
    }

    private void EnsurePropertyUndoState()
    {
        if (_propertyEditSnapshotCaptured)
            return;

        PushUndoState();
        _propertyEditSnapshotCaptured = true;
    }

    private void RestoreEditorState(EditorState state)
    {
        _isApplyingHistory = true;
        _template = _templateService.Clone(state.Template);
        LoadTemplateToControls(state.SelectedIndices);
        _propertyEditSnapshotCaptured = false;
        _isApplyingHistory = false;
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
            return;

        _redoStack.Push(CaptureEditorState());
        RestoreEditorState(_undoStack.Pop());
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
            return;

        _undoStack.Push(CaptureEditorState());
        RestoreEditorState(_redoStack.Pop());
    }

    private FrameworkElement CreateQrElement(LabelElement item)
    {
        var content = ResolveElementContent(item);
        if (!string.IsNullOrWhiteSpace(content))
        {
            var pixelSize = (int)Math.Max(10, Math.Min(item.Width, item.Height) * CanvasScale);
            var cacheKey = $"{content}\u001f{pixelSize}\u001f{item.QrErrorCorrection}\u001f{item.QrMargin}";
            if (!_qrPreviewCache.TryGetValue(cacheKey, out var bitmapImage))
            {
                using var bitmap = QrBitmapService.GenerateQRCode(content, pixelSize, item.QrErrorCorrection, item.QrMargin);
                bitmapImage = WpfBitmapConverter.ToBitmapImage(bitmap);
                if (bitmapImage is not null)
                    _qrPreviewCache[cacheKey] = bitmapImage;
            }

            if (bitmapImage is not null)
                return new Image { Source = bitmapImage, Stretch = Stretch.Uniform };
        }
        return new TextBlock
        {
            Text = "QR",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Bold
        };
    }

    private FrameworkElement CreateBarcodeElement(LabelElement item)
    {
        var content = ResolveElementContent(item);

        var width = Math.Max(1, item.Width * CanvasScale);
        var height = Math.Max(1, item.Height * CanvasScale);
        var textHeight = Math.Max(1, height * BarcodeTextHeightRatio);
        var barcodeHeight = Math.Max(1, height - textHeight);
        var grid = new Grid
        {
            Width = width,
            Height = height,
            ClipToBounds = true
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(barcodeHeight, GridUnitType.Pixel) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(textHeight, GridUnitType.Pixel) });

        if (!string.IsNullOrWhiteSpace(content))
        {
            var pixelWidth = (int)Math.Max(10, width);
            var pixelHeight = (int)Math.Max(10, barcodeHeight);
            var cacheKey = $"{content}\u001f{pixelWidth}\u001f{pixelHeight}";
            if (!_barcodePreviewCache.TryGetValue(cacheKey, out var bitmapImage))
            {
                using var bitmap = BarcodeBitmapService.GenerateCode128(content, pixelWidth, pixelHeight);
                bitmapImage = WpfBitmapConverter.ToBitmapImage(bitmap);
                if (bitmapImage is not null)
                    _barcodePreviewCache[cacheKey] = bitmapImage;
            }

            if (bitmapImage is not null)
            {
                var image = new Image
                {
                    Source = bitmapImage,
                    Stretch = Stretch.Fill
                };
                Grid.SetRow(image, 0);
                grid.Children.Add(image);
            }
        }

        if (grid.Children.Count == 0)
        {
            var placeholder = new TextBlock
            {
                Text = "Barcode",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold
            };
            Grid.SetRow(placeholder, 0);
            grid.Children.Add(placeholder);
        }

        var textBlock = new TextBlock
        {
            Text = content,
            FontFamily = new FontFamily(ResolveElementFontFamilyName(item)),
            FontSize = CreatePreviewFontSize(item, textHeight),
            FontWeight = item.IsBold ? FontWeights.Bold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            ClipToBounds = true
        };
        Grid.SetRow(textBlock, 1);
        grid.Children.Add(textBlock);

        return grid;
    }

    private static double CreatePreviewFontSize(LabelElement item, double? maxHeight = null)
    {
        var fontSize = Math.Max(1, item.FontSize) * CanvasScale;
        return maxHeight.HasValue ? Math.Min(fontSize, Math.Max(1, maxHeight.Value * 0.85)) : fontSize;
    }

    private string ResolveElementContent(LabelElement item)
        => _fieldValues is not null ? _fieldValues.Resolve(item.ContentTemplate) : TemplateEngine.Resolve(
            item.ContentTemplate,
            _title, _partNumber,
            _backupField1, _backupField2,
            _sequenceCode, _productionDate, _operatorName);

    private static FrameworkElement CreateLineElement(LabelElement item)
    {
        return new System.Windows.Shapes.Line
        {
            X1 = 0,
            Y1 = 0,
            X2 = Math.Max(1, item.Width * CanvasScale),
            Y2 = Math.Max(1, item.Height * CanvasScale),
            Stroke = Brushes.Black,
            StrokeThickness = Math.Max(1, item.LineThickness * CanvasScale),
            StrokeDashArray = CreateStrokeDashArray(item.LineStyle)
        };
    }

    private FrameworkElement CreateTableElement(LabelElement item)
    {
        EnsureTableLines(item);
        var canvas = new Canvas
        {
            Width = Math.Max(1, item.Width * CanvasScale),
            Height = Math.Max(1, item.Height * CanvasScale),
            Background = Brushes.Transparent
        };

        var strokeThickness = Math.Max(1, item.LineThickness * CanvasScale);
        var dashArray = CreateStrokeDashArray(item.LineStyle);
        AddTableLine(canvas, item, 0, 0, canvas.Width, 0, strokeThickness, dashArray, null, false);
        AddTableLine(canvas, item, 0, canvas.Height, canvas.Width, canvas.Height, strokeThickness, dashArray, null, false);
        AddTableLine(canvas, item, 0, 0, 0, canvas.Height, strokeThickness, dashArray, null, false);
        AddTableLine(canvas, item, canvas.Width, 0, canvas.Width, canvas.Height, strokeThickness, dashArray, null, false);

        var columnBands = CreateTableBands(item.TableColumnLines, item.Width);
        var rowBands = CreateTableBands(item.TableRowLines, item.Height);

        for (var i = 0; i < item.TableColumnLines.Count; i++)
        {
            var currentX = item.TableColumnLines[i] * CanvasScale;
            for (var segmentIndex = 0; segmentIndex < rowBands.Count; segmentIndex++)
            {
                if (IsHiddenTableSegment(item.HiddenTableColumnSegments, i, segmentIndex))
                    continue;

                var segment = rowBands[segmentIndex];
                AddTableLine(canvas, item, currentX, segment.Start * CanvasScale, currentX, segment.End * CanvasScale, strokeThickness, dashArray, i, true, segmentIndex);
            }
        }

        for (var i = 0; i < item.TableRowLines.Count; i++)
        {
            var currentY = item.TableRowLines[i] * CanvasScale;
            for (var segmentIndex = 0; segmentIndex < columnBands.Count; segmentIndex++)
            {
                if (IsHiddenTableSegment(item.HiddenTableRowSegments, i, segmentIndex))
                    continue;

                var segment = columnBands[segmentIndex];
                AddTableLine(canvas, item, segment.Start * CanvasScale, currentY, segment.End * CanvasScale, currentY, strokeThickness, dashArray, i, false, segmentIndex);
            }
        }

        return canvas;
    }

    private void AddTableLine(Canvas canvas, LabelElement owner, double x1, double y1, double x2, double y2, double strokeThickness, DoubleCollection? dashArray, int? lineIndex, bool isColumn, int segmentIndex = -1)
    {
        var isOwnerSelected = ReferenceEquals(_selectedItem, owner);
        var isSelectedLine = lineIndex.HasValue
            && isOwnerSelected
            && _selectedTableLineIsColumn == isColumn
            && _selectedTableLineIndex == lineIndex.Value
            && _selectedTableLineSegmentIndex == segmentIndex;
        var line = new System.Windows.Shapes.Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = isSelectedLine ? Brushes.OrangeRed : Brushes.Black,
            StrokeThickness = isSelectedLine ? strokeThickness + 1.5 : strokeThickness,
            StrokeDashArray = dashArray,
            Cursor = lineIndex.HasValue && isOwnerSelected ? (isColumn ? Cursors.SizeWE : Cursors.SizeNS) : Cursors.Arrow
        };

        canvas.Children.Add(line);

        if (!lineIndex.HasValue || !isOwnerSelected)
            return;

        var hitArea = new Border
        {
            Background = Brushes.Transparent,
            Cursor = isColumn ? Cursors.SizeWE : Cursors.SizeNS,
            Tag = new TableResizeTag(owner, lineIndex.Value, isColumn, segmentIndex)
        };

        if (isColumn)
        {
            hitArea.Width = Math.Max(8, strokeThickness + 6);
            hitArea.Height = Math.Max(8, Math.Abs(y2 - y1));
            Canvas.SetLeft(hitArea, x1 - (hitArea.Width / 2.0));
            Canvas.SetTop(hitArea, Math.Min(y1, y2));
        }
        else
        {
            hitArea.Width = Math.Max(8, Math.Abs(x2 - x1));
            hitArea.Height = Math.Max(8, strokeThickness + 6);
            Canvas.SetLeft(hitArea, Math.Min(x1, x2));
            Canvas.SetTop(hitArea, y1 - (hitArea.Height / 2.0));
        }

        AttachTableSegmentHandlers(hitArea);
        canvas.Children.Add(hitArea);
    }

    private void AttachTableSegmentHandlers(FrameworkElement element)
    {
        element.MouseLeftButtonDown += TableSegment_MouseLeftButtonDown;
        element.MouseMove += TableSegment_MouseMove;
        element.MouseLeftButtonUp += TableSegment_MouseLeftButtonUp;
    }

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isResizingTableSegment)
            return;

        if (sender is not FrameworkElement element || element.Tag is not LabelElement item)
            return;

        _selectedItem = item;
        DesignCanvas.Focus();
        if (item.Type == LabelElementType.Table)
        {
            _selectedTableLineIsColumn = null;
            _selectedTableLineIndex = -1;
            _selectedTableLineSegmentIndex = -1;
        }
        _suppressSelectionRedraw = true;
        try
        {
            ItemsListBox.SelectedItem = item;
        }
        finally
        {
            _suppressSelectionRedraw = false;
        }
        LoadSelectedItemToControls();
        _isDragging = true;
        _dragSnapshotCaptured = false;
        _dragStart = e.GetPosition(DesignCanvas);
        _dragStartX = item.X;
        _dragStartY = item.Y;
        element.CaptureMouse();
        e.Handled = true;
    }

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isResizingTableSegment
            || !_isDragging
            || sender is not FrameworkElement { Tag: LabelElement draggedItem } element
            || !element.IsMouseCaptured)
        {
            return;
        }

        var current = e.GetPosition(DesignCanvas);
        if (!_dragSnapshotCaptured && Math.Abs(current.X - _dragStart.X) + Math.Abs(current.Y - _dragStart.Y) > 1)
        {
            PushUndoState();
            _dragSnapshotCaptured = true;
        }

        _selectedItem = draggedItem;
        draggedItem.X = Math.Clamp(_dragStartX + ((current.X - _dragStart.X) / CanvasScale), 0, Math.Max(0, _template.LabelWidthMm - draggedItem.Width));
        draggedItem.Y = Math.Clamp(_dragStartY + ((current.Y - _dragStart.Y) / CanvasScale), 0, Math.Max(0, _template.LabelHeightMm - draggedItem.Height));
        UpdateSelectedPositionControls();
        Canvas.SetLeft(element, draggedItem.X * CanvasScale);
        Canvas.SetTop(element, draggedItem.Y * CanvasScale);
        e.Handled = true;
    }

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        if (sender is FrameworkElement element)
            element.ReleaseMouseCapture();

        RedrawCanvas();
        RefreshPreview();
        e.Handled = true;
    }

    private void TableSegment_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TableResizeTag tag } element)
        {
            return;
        }

        var item = tag.Owner;
        if (!ReferenceEquals(_selectedItem, item))
            return;

        _selectedItem = item;
        ItemsListBox.SelectedItem = item;
        _isDragging = false;
        _isResizingTableSegment = true;
        _tableResizeSnapshotCaptured = false;
        _isResizingColumn = tag.IsColumn;
        _resizingSegmentIndex = tag.Index;
        _selectedTableLineIsColumn = tag.IsColumn;
        _selectedTableLineIndex = tag.Index;
        _selectedTableLineSegmentIndex = tag.SegmentIndex;
        _dragStart = e.GetPosition(DesignCanvas);
        LoadSelectedItemToControls();
        RedrawCanvas();
        DesignCanvas.Cursor = _isResizingColumn ? Cursors.SizeWE : Cursors.SizeNS;
        DesignCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void TableSegment_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizingTableSegment)
            return;

        ResizeSelectedTableSegment(e.GetPosition(DesignCanvas));
        e.Handled = true;
    }

    private void TableSegment_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CompleteTableSegmentResize();
        e.Handled = true;
    }

    private void DesignCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizingTableSegment)
            return;

        ResizeSelectedTableSegment(e.GetPosition(DesignCanvas));
        e.Handled = true;
    }

    private void DesignCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizingTableSegment)
            return;

        CompleteTableSegmentResize();
        e.Handled = true;
    }

    private void ResizeSelectedTableSegment(Point current)
    {
        if (_selectedItem is not { Type: LabelElementType.Table } item)
            return;

        var target = _isResizingColumn ? item.TableColumnLines : item.TableRowLines;
        var total = _isResizingColumn ? item.Width : item.Height;
        var position = (_isResizingColumn ? current.X / CanvasScale - item.X : current.Y / CanvasScale - item.Y);
        if (_resizingSegmentIndex < 0 || _resizingSegmentIndex >= target.Count)
            return;

        if (!_tableResizeSnapshotCaptured)
        {
            PushUndoState();
            _tableResizeSnapshotCaptured = true;
        }

        target[_resizingSegmentIndex] = ClampTableLinePositionForIndex(target, _resizingSegmentIndex, position, total);
        SortTableLinesKeepingSelection(item, _isResizingColumn, target[_resizingSegmentIndex]);
        SyncTableSegmentsFromLines(item);
        LoadSelectedItemToControls();
        RedrawCanvas();
        RefreshPreview();
    }

    private void CompleteTableSegmentResize()
    {
        _isResizingTableSegment = false;
        DesignCanvas.ReleaseMouseCapture();
        DesignCanvas.Cursor = Cursors.Arrow;
        RedrawCanvas();
        RefreshPreview();
    }

    private void ItemsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingControls || _suppressSelectionRedraw)
            return;

        _selectedItem = ItemsListBox.SelectedItem as LabelElement;
        _propertyEditSnapshotCaptured = false;
        LoadSelectedItemToControls();
        RedrawCanvas();
    }

    private void LoadSelectedItemToControls()
    {
        _isLoadingControls = true;
        var item = _selectedItem;
        var isTable = item?.Type == LabelElementType.Table;
        if (!isTable)
        {
            _selectedTableLineIsColumn = null;
            _selectedTableLineIndex = -1;
            _selectedTableLineSegmentIndex = -1;
        }
        else
        {
            EnsureTableLines(item!);
            if (_selectedTableLineIsColumn == true && _selectedTableLineIndex >= item!.TableColumnLines.Count)
                _selectedTableLineIndex = -1;
            if (_selectedTableLineIsColumn == false && _selectedTableLineIndex >= item!.TableRowLines.Count)
                _selectedTableLineIndex = -1;
            var maxSegmentCount = _selectedTableLineIsColumn == true
                ? item!.TableRowLines.Count + 1
                : item!.TableColumnLines.Count + 1;
            if (_selectedTableLineSegmentIndex >= maxSegmentCount)
                _selectedTableLineSegmentIndex = -1;
        }
        ContentTextBox.Text = item?.ContentTemplate ?? string.Empty;
        XTextBox.Text = item is null ? string.Empty : Format(item.X);
        YTextBox.Text = item is null ? string.Empty : Format(item.Y);
        WidthTextBox.Text = item is null ? string.Empty : Format(item.Width);
        HeightTextBox.Text = item is null ? string.Empty : Format(item.Height);
        FontSizeTextBox.Text = item is null ? string.Empty : Format(item.FontSize);
        FontFamilyComboBox.SelectedValue = item is null ? null : ResolveElementFontFamilyName(item);
        LineThicknessTextBox.Text = item is null ? string.Empty : Format(item.LineThickness);
        LineStyleComboBox.SelectedIndex = ResolveLineStyleIndex(item?.LineStyle);
        RowCountTextBox.Text = item is null ? string.Empty : item.RowCount.ToString(CultureInfo.InvariantCulture);
        ColumnCountTextBox.Text = item is null ? string.Empty : item.ColumnCount.ToString(CultureInfo.InvariantCulture);
        TableColumnLineTextBox.Text = item is { Type: LabelElementType.Table } tableForColumn
            ? Format(GetDefaultInsertPosition(tableForColumn.TableColumnLines, tableForColumn.Width))
            : string.Empty;
        TableRowLineTextBox.Text = item is { Type: LabelElementType.Table } tableForRow
            ? Format(GetDefaultInsertPosition(tableForRow.TableRowLines, tableForRow.Height))
            : string.Empty;
        BoldCheckBox.IsChecked = item?.IsBold == true;
        FieldComboBox.IsEnabled = CanInsertField(item);
        InsertFieldButton.IsEnabled = CanInsertField(item);
        ContentTextBox.IsEnabled = IsContentEditable(item);
        SelectImageButton.IsEnabled = item?.Type == LabelElementType.Image;
        FontSizeTextBox.IsEnabled = item?.Type is LabelElementType.Text or LabelElementType.Barcode;
        FontFamilyComboBox.IsEnabled = item?.Type == LabelElementType.Text;
        BoldCheckBox.IsEnabled = item?.Type == LabelElementType.Text;
        LineThicknessTextBox.IsEnabled = item?.Type is LabelElementType.Line or LabelElementType.Table;
        LineStyleComboBox.IsEnabled = item?.Type is LabelElementType.Line or LabelElementType.Table;
        RowCountTextBox.IsEnabled = isTable;
        ColumnCountTextBox.IsEnabled = isTable;
        TableColumnLineTextBox.IsEnabled = isTable;
        TableRowLineTextBox.IsEnabled = isTable;
        InsertColumnLineButton.IsEnabled = isTable;
        InsertRowLineButton.IsEnabled = isTable;
        DeleteTableLineButton.IsEnabled = isTable
            && _selectedTableLineIsColumn.HasValue
            && _selectedTableLineIndex >= 0
            && _selectedTableLineSegmentIndex >= 0;
        DeleteWholeTableLineButton.IsEnabled = isTable
            && _selectedTableLineIsColumn.HasValue
            && _selectedTableLineIndex >= 0;
        _isLoadingControls = false;
    }

    private void UpdateSelectedPositionControls()
    {
        if (_selectedItem is null)
            return;

        _isLoadingControls = true;
        XTextBox.Text = Format(_selectedItem.X);
        YTextBox.Text = Format(_selectedItem.Y);
        _isLoadingControls = false;
    }

    private void PropertyTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingControls || _selectedItem is null)
            return;

        EnsurePropertyUndoState();
        ApplySelectedItemControls();
        RedrawCanvas();
        RefreshPreview();
    }

    private void BoldCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingControls || _selectedItem is null)
            return;

        PushUndoState();
        _selectedItem.IsBold = BoldCheckBox.IsChecked == true;
        RedrawCanvas();
        RefreshPreview();
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingControls || _selectedItem is not { Type: LabelElementType.Text } item)
            return;

        if (FontFamilyComboBox.SelectedValue is not string fontFamily || string.IsNullOrWhiteSpace(fontFamily))
            return;

        PushUndoState();
        item.FontFamily = fontFamily;
        RedrawCanvas();
        RefreshPreview();
    }

    private void LineStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingControls || _selectedItem is null || LineStyleComboBox.SelectedItem is not ComboBoxItem item)
            return;

        PushUndoState();
        _selectedItem.LineStyle = item.Content?.ToString() ?? "Solid";
        RedrawCanvas();
        RefreshPreview();
    }

    private void TableShapeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingControls || _selectedItem is not { Type: LabelElementType.Table })
            return;

        EnsurePropertyUndoState();
        ApplyTableShapeControls(_selectedItem);
        RedrawCanvas();
        RefreshPreview();
    }

    private void AddTextButton_Click(object sender, RoutedEventArgs e)
    {
        PushUndoState();
        var item = new LabelElement
        {
            Name = $"Text{_items.Count + 1}",
            Type = LabelElementType.Text,
            ContentTemplate = "{{PartNumber}}",
            X = 2,
            Y = 2,
            Width = 18,
            Height = 4,
            FontSize = 2.4,
            FontFamily = DefaultFontFamily,
            IsBold = true
        };
        _items.Add(item);
        ItemsListBox.SelectedItem = item;
        RedrawCanvas();
        RefreshPreview();
    }

    private void AddQrButton_Click(object sender, RoutedEventArgs e)
    {
        PushUndoState();
        var item = new LabelElement
        {
            Name = $"Qr{_items.Count + 1}",
            Type = LabelElementType.QrCode,
            ContentTemplate = "{{PartNumber}}{{SequenceCode4}}",
            X = Math.Max(0, _template.LabelWidthMm - 14),
            Y = 3,
            Width = 12,
            Height = 12
        };
        _items.Add(item);
        ItemsListBox.SelectedItem = item;
        RedrawCanvas();
        RefreshPreview();
    }

    private void AddBarcodeButton_Click(object sender, RoutedEventArgs e)
    {
        PushUndoState();
        var item = new LabelElement
        {
            Name = $"Barcode{_items.Count + 1}",
            Type = LabelElementType.Barcode,
            ContentTemplate = "{{PartNumber}}",
            X = 2,
            Y = 2,
            Width = 22,
            Height = 8,
            FontSize = 1.6,
            IsBold = false
        };
        _items.Add(item);
        ItemsListBox.SelectedItem = item;
        RedrawCanvas();
        RefreshPreview();
    }

    private void AddImageButton_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectImagePath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        PushUndoState();
        var item = new LabelElement
        {
            Name = $"Image{_items.Count + 1}",
            Type = LabelElementType.Image,
            ContentTemplate = path,
            ImagePath = path,
            X = 2,
            Y = 2,
            Width = 10,
            Height = 6
        };
        _items.Add(item);
        ItemsListBox.SelectedItem = item;
        RedrawCanvas();
        RefreshPreview();
    }

    private void AddLineButton_Click(object sender, RoutedEventArgs e)
    {
        PushUndoState();
        var item = new LabelElement
        {
            Name = $"Line{_items.Count + 1}",
            Type = LabelElementType.Line,
            X = 3,
            Y = 3,
            Width = 20,
            Height = 0,
            LineThickness = 0.2,
            LineStyle = "Solid"
        };
        _items.Add(item);
        ItemsListBox.SelectedItem = item;
        RedrawCanvas();
        RefreshPreview();
    }

    private void AddTableButton_Click(object sender, RoutedEventArgs e)
    {
        PushUndoState();
        var item = new LabelElement
        {
            Name = $"Table{_items.Count + 1}",
            Type = LabelElementType.Table,
            X = 3,
            Y = 3,
            Width = 24,
            Height = 12,
            LineThickness = 0.2,
            LineStyle = "Solid",
            RowCount = 2,
            ColumnCount = 2,
            TableRowLines = new List<double> { 6 },
            TableColumnLines = new List<double> { 12 }
        };
        EnsureTableLines(item);
        _items.Add(item);
        ItemsListBox.SelectedItem = item;
        RedrawCanvas();
        RefreshPreview();
    }

    private void SelectImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null)
            return;

        var path = SelectImagePath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        PushUndoState();
        _propertyEditSnapshotCaptured = true;
        ContentTextBox.Text = path;
        _selectedItem.ImagePath = path;
        ApplySelectedItemControls();
        RedrawCanvas();
        RefreshPreview();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
        => DeleteSelectedItems();

    private void InsertFieldButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInsertField(_selectedItem) || FieldComboBox.SelectedValue is not string token)
            return;

        if (HasConflictingSequenceCodeField(token))
        {
            MessageBox.Show(
                "同一模板不能同时使用4位顺序号和5位顺序号。请先删除已使用的另一种顺序号字段。",
                "顺序号字段冲突",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        EnsurePropertyUndoState();
        var caret = Math.Clamp(ContentTextBox.CaretIndex, 0, ContentTextBox.Text.Length);
        ContentTextBox.Text = ContentTextBox.Text.Insert(caret, token);
        ContentTextBox.CaretIndex = caret + token.Length;
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void RedoButton_Click(object sender, RoutedEventArgs e) => Redo();

    private void CutButton_Click(object sender, RoutedEventArgs e) => CutSelectedItems();

    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopySelectedItems();

    private void PasteButton_Click(object sender, RoutedEventArgs e) => PasteItems();

    private void CopySelectedItems()
    {
        if (_selectedItem is null)
            return;

        _clipboardItems = [CloneElement(_selectedItem)];
    }

    private void CutSelectedItems()
    {
        if (_selectedItem is null)
            return;

        CopySelectedItems();
        DeleteSelectedItems();
    }

    private void PasteItems()
    {
        if (_clipboardItems.Count == 0)
            return;

        PushUndoState();
        var pastedItems = new List<LabelElement>();
        foreach (var source in _clipboardItems)
        {
            var item = CloneElement(source);
            item.Name = CreateCopiedName(source.Name);
            item.X = Math.Clamp(item.X + 2, 0, Math.Max(0, _template.LabelWidthMm - item.Width));
            item.Y = Math.Clamp(item.Y + 2, 0, Math.Max(0, _template.LabelHeightMm - item.Height));
            _items.Add(item);
            pastedItems.Add(item);
        }

        _selectedItem = pastedItems.LastOrDefault();
        if (_selectedItem is not null)
            ItemsListBox.SelectedItem = _selectedItem;

        LoadSelectedItemToControls();
        RedrawCanvas();
        RefreshPreview();
    }

    private void DeleteSelectedItems()
    {
        var itemToDelete = _selectedItem ?? ItemsListBox.SelectedItem as LabelElement;
        if (itemToDelete is null)
            return;

        var deleteIndex = _items.IndexOf(itemToDelete);
        if (deleteIndex < 0)
            return;

        var itemsToDelete = GetStackedEquivalentItems(itemToDelete);
        if (itemsToDelete.Count == 0)
            itemsToDelete.Add(itemToDelete);

        PushUndoState();
        _suppressSelectionRedraw = true;
        try
        {
            foreach (var item in itemsToDelete.OrderByDescending(item => _items.IndexOf(item)))
            {
                var index = _items.IndexOf(item);
                if (index >= 0)
                    _items.RemoveAt(index);
            }

            var nextIndex = _items.Count == 0 ? -1 : Math.Min(deleteIndex, _items.Count - 1);
            ItemsListBox.SelectedIndex = nextIndex;
            _selectedItem = nextIndex >= 0 ? _items[nextIndex] : null;
        }
        finally
        {
            _suppressSelectionRedraw = false;
        }

        LoadSelectedItemToControls();
        RedrawCanvas();
        RefreshPreview();
    }

    private List<LabelElement> GetStackedEquivalentItems(LabelElement itemToDelete)
        => _items
            .Where(item => ReferenceEquals(item, itemToDelete) || IsStackedEquivalentElement(item, itemToDelete))
            .ToList();

    private static void RemoveStackedDuplicateCodeElements(IList<LabelElement> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = items[index];
            if (item.Type is not (LabelElementType.QrCode or LabelElementType.Barcode or LabelElementType.Image))
                continue;

            var hasEarlierEquivalent = items
                .Take(index)
                .Any(existing => ReferenceEquals(existing, item) || IsStackedEquivalentElement(existing, item));
            if (hasEarlierEquivalent)
                items.RemoveAt(index);
        }
    }

    private static bool IsStackedEquivalentElement(LabelElement left, LabelElement right)
        => !ReferenceEquals(left, right)
            && left.Type == right.Type
            && left.Type is LabelElementType.QrCode or LabelElementType.Barcode or LabelElementType.Image
            && NearlyEqual(left.X, right.X)
            && NearlyEqual(left.Y, right.Y)
            && NearlyEqual(left.Width, right.Width)
            && NearlyEqual(left.Height, right.Height)
            && string.Equals(left.ContentTemplate, right.ContentTemplate, StringComparison.Ordinal)
            && string.Equals(left.ImagePath, right.ImagePath, StringComparison.OrdinalIgnoreCase);

    private static bool NearlyEqual(double left, double right)
        => Math.Abs(left - right) < 0.001;

    private void LabelTemplateEditorWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsKeyboardFocusInsideTextEditingControl())
            return;

        if (Keyboard.Modifiers == ModifierKeys.None && _selectedItem is { } item
            && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            const double step = 0.1;
            var x = Math.Clamp(item.X + (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0),
                0, Math.Max(0, _template.LabelWidthMm - item.Width));
            var y = Math.Clamp(item.Y + (e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0),
                0, Math.Max(0, _template.LabelHeightMm - item.Height));
            if (Math.Abs(x - item.X) > 0.000001 || Math.Abs(y - item.Y) > 0.000001)
            {
                PushUndoState();
                item.X = Math.Round(x, 3);
                item.Y = Math.Round(y, 3);
                UpdateSelectedPositionControls();
                RedrawCanvas();
                RefreshPreview();
            }
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z:
                    Undo();
                    e.Handled = true;
                    return;
                case Key.Y:
                    Redo();
                    e.Handled = true;
                    return;
                case Key.X:
                    CutSelectedItems();
                    e.Handled = true;
                    return;
                case Key.C:
                    CopySelectedItems();
                    e.Handled = true;
                    return;
                case Key.V:
                    PasteItems();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Delete)
        {
            DeleteSelectedItems();
            e.Handled = true;
        }
    }

    private string CreateCopiedName(string sourceName)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "Element" : sourceName.Trim();
        var candidate = $"{baseName}_复制";
        var index = 2;
        while (_items.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName}_复制{index}";
            index++;
        }

        return candidate;
    }

    private static bool IsKeyboardFocusInsideTextEditingControl()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        while (focused is not null)
        {
            if (focused is TextBoxBase or PasswordBox or ComboBox)
                return true;

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private void InsertColumnLineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is not { Type: LabelElementType.Table } table || !TryParse(TableColumnLineTextBox.Text, out var position))
            return;

        AddTableLinePosition(table, isColumn: true, position);
    }

    private void InsertRowLineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is not { Type: LabelElementType.Table } table || !TryParse(TableRowLineTextBox.Text, out var position))
            return;

        AddTableLinePosition(table, isColumn: false, position);
    }

    private void DeleteTableLineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is not { Type: LabelElementType.Table } table
            || !_selectedTableLineIsColumn.HasValue
            || _selectedTableLineIndex < 0
            || _selectedTableLineSegmentIndex < 0)
        {
            return;
        }

        var targetLines = _selectedTableLineIsColumn.Value ? table.TableColumnLines : table.TableRowLines;
        var hiddenSegments = _selectedTableLineIsColumn.Value ? table.HiddenTableColumnSegments : table.HiddenTableRowSegments;
        if (_selectedTableLineIndex >= targetLines.Count)
            return;

        PushUndoState();
        var key = CreateTableSegmentKey(_selectedTableLineIndex, _selectedTableLineSegmentIndex);
        if (!hiddenSegments.Contains(key, StringComparer.OrdinalIgnoreCase))
            hiddenSegments.Add(key);
        _selectedTableLineIndex = -1;
        _selectedTableLineSegmentIndex = -1;
        _selectedTableLineIsColumn = null;
        SyncTableSegmentsFromLines(table);
        LoadSelectedItemToControls();
        RedrawCanvas();
        RefreshPreview();
    }

    private void DeleteWholeTableLineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is not { Type: LabelElementType.Table } table
            || !_selectedTableLineIsColumn.HasValue
            || _selectedTableLineIndex < 0)
        {
            return;
        }

        var targetLines = _selectedTableLineIsColumn.Value ? table.TableColumnLines : table.TableRowLines;
        if (_selectedTableLineIndex >= targetLines.Count)
            return;

        PushUndoState();
        ShiftHiddenSegmentsForDeletedLine(table, _selectedTableLineIsColumn.Value, _selectedTableLineIndex);
        targetLines.RemoveAt(_selectedTableLineIndex);
        _selectedTableLineIndex = -1;
        _selectedTableLineSegmentIndex = -1;
        _selectedTableLineIsColumn = null;
        SyncTableSegmentsFromLines(table);
        LoadSelectedItemToControls();
        RedrawCanvas();
        RefreshPreview();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyPaperControls();
        ApplySelectedItemControls();
        RemoveStackedDuplicateCodeElements(_items);
        if (LabelTemplateService.HasConflictingSequenceCodeFields(_items))
        {
            MessageBox.Show(
                "同一模板不能同时使用4位顺序号和5位顺序号，请删除其中一种后再保存。",
                "无法保存模板",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _template.TemplateCode = LabelTemplateService.NormalizeTemplateCode(TemplateCodeTextBox.Text);
        _template.DisplayName = _template.TemplateCode;
        _template.Elements = _items.ToList();
        _templateService.Save(_template);
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void DesignCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == DesignCanvas)
            ItemsListBox.SelectedIndex = -1;
    }

    private void ApplyPaperControls()
    {
        if (TryParse(PaperWidthTextBox.Text, out var width))
            _template.LabelWidthMm = Math.Max(1, width);
        if (TryParse(PaperHeightTextBox.Text, out var height))
            _template.LabelHeightMm = Math.Max(1, height);
    }

    private void ApplySelectedItemControls()
    {
        var item = _selectedItem;
        if (item is null)
            return;

        if (IsContentEditable(item))
            item.ContentTemplate = ContentTextBox.Text;
        if (item.Type == LabelElementType.Image)
            item.ImagePath = ContentTextBox.Text;
        if (TryParse(LineThicknessTextBox.Text, out var lineThickness))
            item.LineThickness = Math.Max(0.05, lineThickness);
        if (TryParse(XTextBox.Text, out var x))
            item.X = Math.Max(0, x);
        if (TryParse(YTextBox.Text, out var y))
            item.Y = Math.Max(0, y);
        if (TryParse(WidthTextBox.Text, out var width))
            item.Width = Math.Max(1, width);
        if (TryParse(HeightTextBox.Text, out var height))
            item.Height = Math.Max(1, height);
        if (TryParse(FontSizeTextBox.Text, out var fontSize))
            item.FontSize = Math.Max(0, fontSize);
        if (item.Type == LabelElementType.Text
            && FontFamilyComboBox.SelectedValue is string fontFamily
            && !string.IsNullOrWhiteSpace(fontFamily))
        {
            item.FontFamily = fontFamily;
        }

        if (item.Type == LabelElementType.Table)
            EnsureTableLines(item);
    }

    private static FontFamilyChoice[] GetSystemFontNames()
        => Fonts.SystemFontFamilies
            .Select(font => new FontFamilyChoice(font.Source, GetLocalizedFontName(font)))
            .GroupBy(font => font.SourceName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(font => font.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static string GetLocalizedFontName(FontFamily font)
    {
        string[] preferredLanguages = [
            "zh-CN",
            "zh-Hans",
            CultureInfo.CurrentUICulture.IetfLanguageTag
        ];

        foreach (var language in preferredLanguages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (font.FamilyNames.TryGetValue(XmlLanguage.GetLanguage(language), out var localizedName)
                && !string.IsNullOrWhiteSpace(localizedName))
            {
                return localizedName.Equals(font.Source, StringComparison.OrdinalIgnoreCase)
                    ? localizedName
                    : $"{localizedName} ({font.Source})";
            }
        }

        return font.Source;
    }

    private static string ResolveElementFontFamilyName(LabelElement item)
        => string.IsNullOrWhiteSpace(item.FontFamily) ? DefaultFontFamily : item.FontFamily.Trim();

    private sealed record FontFamilyChoice(string SourceName, string DisplayName);

    private static bool IsContentEditable(LabelElement? item)
        => item?.Type is LabelElementType.Text
            or LabelElementType.QrCode
            or LabelElementType.Barcode
            or LabelElementType.Image;

    private static bool CanInsertField(LabelElement? item)
        => item?.Type is LabelElementType.Text
            or LabelElementType.QrCode
            or LabelElementType.Barcode;

    private bool HasConflictingSequenceCodeField(string token)
    {
        var conflictingToken = token.Equals(LabelTemplateService.SequenceCodeToken, StringComparison.OrdinalIgnoreCase)
            ? LabelTemplateService.SequenceCode5Token
            : token.Equals(LabelTemplateService.SequenceCode5Token, StringComparison.OrdinalIgnoreCase)
                ? LabelTemplateService.SequenceCodeToken
                : null;

        return conflictingToken is not null && LabelTemplateService.UsesField(_items, conflictingToken);
    }

    private void RefreshPreview()
    {
    }

    private static bool TryParse(string? value, out double result)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static DoubleCollection? CreateStrokeDashArray(string? lineStyle)
        => lineStyle?.Trim().ToLowerInvariant() switch
        {
            "dash" or "dashed" => new DoubleCollection { 4, 2 },
            "dot" or "dotted" => new DoubleCollection { 1, 2 },
            "dashdot" or "dash-dot" => new DoubleCollection { 4, 2, 1, 2 },
            _ => null
        };

    private static int ResolveLineStyleIndex(string? lineStyle)
        => lineStyle?.Trim().ToLowerInvariant() switch
        {
            "dash" or "dashed" => 1,
            "dot" or "dotted" => 2,
            "dashdot" or "dash-dot" => 3,
            _ => 0
        };

    private static void EnsureTableLines(LabelElement item)
    {
        var width = Math.Max(1, item.Width);
        var height = Math.Max(1, item.Height);

        if (item.TableColumnLines.Count == 0 && item.ColumnCount > 1)
            item.TableColumnLines = SegmentLinesFromLegacyWidths(item.ColumnWidths, item.ColumnCount, width);
        if (item.TableRowLines.Count == 0 && item.RowCount > 1)
            item.TableRowLines = SegmentLinesFromLegacyWidths(item.RowHeights, item.RowCount, height);

        item.TableColumnLines = NormalizeLinePositions(item.TableColumnLines, width);
        item.TableRowLines = NormalizeLinePositions(item.TableRowLines, height);
        SyncTableSegmentsFromLines(item);
    }

    private static void SyncTableSegmentsFromLines(LabelElement item)
    {
        item.TableColumnLines = NormalizeLinePositions(item.TableColumnLines, Math.Max(1, item.Width));
        item.TableRowLines = NormalizeLinePositions(item.TableRowLines, Math.Max(1, item.Height));
        item.ColumnCount = Math.Clamp(item.TableColumnLines.Count + 1, 1, 20);
        item.RowCount = Math.Clamp(item.TableRowLines.Count + 1, 1, 20);
        item.ColumnWidths = SegmentsFromLinePositions(item.TableColumnLines, Math.Max(1, item.Width));
        item.RowHeights = SegmentsFromLinePositions(item.TableRowLines, Math.Max(1, item.Height));
        item.HiddenTableColumnSegments = NormalizeHiddenSegments(item.HiddenTableColumnSegments, item.TableColumnLines.Count, item.TableRowLines.Count + 1);
        item.HiddenTableRowSegments = NormalizeHiddenSegments(item.HiddenTableRowSegments, item.TableRowLines.Count, item.TableColumnLines.Count + 1);
    }

    private void ApplyTableShapeControls(LabelElement item)
    {
        if (int.TryParse(RowCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowCount))
            item.TableRowLines = CreateEvenLines(Math.Clamp(rowCount, 1, 20), Math.Max(1, item.Height));
        if (int.TryParse(ColumnCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var columnCount))
            item.TableColumnLines = CreateEvenLines(Math.Clamp(columnCount, 1, 20), Math.Max(1, item.Width));

        _selectedTableLineIndex = -1;
        _selectedTableLineIsColumn = null;
        _selectedTableLineSegmentIndex = -1;
        item.HiddenTableColumnSegments.Clear();
        item.HiddenTableRowSegments.Clear();
        SyncTableSegmentsFromLines(item);
        LoadSelectedItemToControls();
    }

    private void AddTableLinePosition(LabelElement item, bool isColumn, double position)
    {
        var total = isColumn ? item.Width : item.Height;
        var target = isColumn ? item.TableColumnLines : item.TableRowLines;
        var normalized = ClampTableLinePosition(position, total);
        var existingIndex = target.FindIndex(x => Math.Abs(x - normalized) < 0.2);
        if (existingIndex >= 0)
        {
            PushUndoState();
            RestoreHiddenSegmentsForLine(item, isColumn, existingIndex);
            _selectedTableLineIsColumn = isColumn;
            _selectedTableLineIndex = existingIndex;
            _selectedTableLineSegmentIndex = -1;
            LoadSelectedItemToControls();
            RedrawCanvas();
            RefreshPreview();
            return;
        }

        PushUndoState();
        target.Add(normalized);
        var insertIndex = NormalizeLinePositions(target, total).FindIndex(x => Math.Abs(x - normalized) < 0.001);
        if (insertIndex >= 0)
            ShiftHiddenSegmentsForInsertedLine(item, isColumn, insertIndex);
        SortTableLinesKeepingSelection(item, isColumn, normalized);
        _selectedTableLineSegmentIndex = -1;
        SyncTableSegmentsFromLines(item);
        LoadSelectedItemToControls();
        RedrawCanvas();
        RefreshPreview();
    }

    private void SortTableLinesKeepingSelection(LabelElement item, bool isColumn, double selectedPosition)
    {
        var target = isColumn ? item.TableColumnLines : item.TableRowLines;
        var normalized = NormalizeLinePositions(target, isColumn ? item.Width : item.Height);
        var selectedIndex = normalized.FindIndex(x => Math.Abs(x - selectedPosition) < 0.001);
        if (selectedIndex < 0)
            selectedIndex = normalized
                .Select((value, index) => new { value, index })
                .OrderBy(x => Math.Abs(x.value - selectedPosition))
                .FirstOrDefault()?.index ?? -1;

        if (isColumn)
            item.TableColumnLines = normalized;
        else
            item.TableRowLines = normalized;

        _selectedTableLineIsColumn = isColumn;
        _selectedTableLineIndex = selectedIndex;
    }

    private static void RestoreHiddenSegmentsForLine(LabelElement item, bool isColumn, int lineIndex)
    {
        var hiddenSegments = isColumn ? item.HiddenTableColumnSegments : item.HiddenTableRowSegments;
        hiddenSegments.RemoveAll(x => TryParseTableSegmentKey(x, out var parsedLine, out _) && parsedLine == lineIndex);
    }

    private static double ClampTableLinePosition(double position, double total)
        => Math.Clamp(position, 0.5, Math.Max(0.5, total - 0.5));

    private static double ClampTableLinePositionForIndex(IReadOnlyList<double> lines, int index, double position, double total)
    {
        var min = index > 0 ? lines[index - 1] + 0.5 : 0.5;
        var max = index + 1 < lines.Count ? lines[index + 1] - 0.5 : Math.Max(0.5, total - 0.5);
        if (max < min)
            return ClampTableLinePosition(position, total);

        return Math.Clamp(position, min, max);
    }

    private static void ShiftHiddenSegmentsForInsertedLine(LabelElement item, bool isColumn, int insertIndex)
    {
        var source = isColumn ? item.HiddenTableColumnSegments : item.HiddenTableRowSegments;
        var shifted = source
            .Select(x =>
            {
                if (!TryParseTableSegmentKey(x, out var lineIndex, out var segmentIndex))
                    return x;

                return CreateTableSegmentKey(lineIndex >= insertIndex ? lineIndex + 1 : lineIndex, segmentIndex);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (isColumn)
            item.HiddenTableColumnSegments = shifted;
        else
            item.HiddenTableRowSegments = shifted;
    }

    private static void ShiftHiddenSegmentsForDeletedLine(LabelElement item, bool isColumn, int deletedIndex)
    {
        var source = isColumn ? item.HiddenTableColumnSegments : item.HiddenTableRowSegments;
        var shifted = source
            .Select(x =>
            {
                if (!TryParseTableSegmentKey(x, out var lineIndex, out var segmentIndex))
                    return null;
                if (lineIndex == deletedIndex)
                    return null;

                return CreateTableSegmentKey(lineIndex > deletedIndex ? lineIndex - 1 : lineIndex, segmentIndex);
            })
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (isColumn)
            item.HiddenTableColumnSegments = shifted;
        else
            item.HiddenTableRowSegments = shifted;
    }

    private static double GetDefaultInsertPosition(IReadOnlyList<double> existingLines, double total)
    {
        var lines = NormalizeLinePositions(existingLines, Math.Max(1, total));
        if (lines.Count == 0)
            return Math.Max(0.5, total / 2.0);

        var points = new List<double> { 0 };
        points.AddRange(lines);
        points.Add(Math.Max(1, total));
        var widestGap = points
            .Zip(points.Skip(1), (left, right) => new { left, right, width = right - left })
            .OrderByDescending(x => x.width)
            .First();
        return ClampTableLinePosition(widestGap.left + widestGap.width / 2.0, total);
    }

    private static List<double> CreateEvenLines(int cellCount, double total)
    {
        if (cellCount <= 1)
            return [];

        var step = total / cellCount;
        return Enumerable.Range(1, cellCount - 1)
            .Select(x => ClampTableLinePosition(step * x, total))
            .ToList();
    }

    private static List<double> SegmentLinesFromLegacyWidths(IReadOnlyList<double>? values, int count, double total)
    {
        var segments = NormalizeSegments(values, count, total);
        var result = new List<double>();
        var current = 0.0;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            current += segments[i];
            result.Add(current);
        }

        return NormalizeLinePositions(result, total);
    }

    private static List<double> SegmentsFromLinePositions(IReadOnlyList<double> lines, double total)
    {
        var points = new List<double> { 0 };
        points.AddRange(NormalizeLinePositions(lines, total));
        points.Add(total);
        return points.Zip(points.Skip(1), (left, right) => Math.Max(0.5, right - left)).ToList();
    }

    private static List<TableBand> CreateTableBands(IReadOnlyList<double> lines, double total)
    {
        var points = new List<double> { 0 };
        points.AddRange(NormalizeLinePositions(lines, total));
        points.Add(Math.Max(1, total));
        return points.Zip(points.Skip(1), (start, end) => new TableBand(start, end)).ToList();
    }

    private static List<double> NormalizeLinePositions(IReadOnlyList<double>? values, double total)
        => values?
            .Select(x => ClampTableLinePosition(x, total))
            .OrderBy(x => x)
            .DistinctBy(x => Math.Round(x, 3))
            .Take(19)
            .ToList() ?? [];

    private static bool IsHiddenTableSegment(IReadOnlyList<string>? hiddenSegments, int lineIndex, int segmentIndex)
        => hiddenSegments?.Contains(CreateTableSegmentKey(lineIndex, segmentIndex), StringComparer.OrdinalIgnoreCase) == true;

    private static string CreateTableSegmentKey(int lineIndex, int segmentIndex)
        => $"{lineIndex}:{segmentIndex}";

    private static List<string> NormalizeHiddenSegments(IReadOnlyList<string>? values, int lineCount, int segmentCount)
        => values?
            .Where(x => TryParseTableSegmentKey(x, out var lineIndex, out var segmentIndex)
                && lineIndex >= 0
                && lineIndex < lineCount
                && segmentIndex >= 0
                && segmentIndex < segmentCount)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

    private static bool TryParseTableSegmentKey(string? value, out int lineIndex, out int segmentIndex)
    {
        lineIndex = -1;
        segmentIndex = -1;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out lineIndex)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out segmentIndex);
    }

    private static List<double> NormalizeSegments(IReadOnlyList<double>? values, int count, double total)
    {
        var actualCount = Math.Max(1, count);
        var result = values?
            .Take(actualCount)
            .Select(x => Math.Max(0.5, x))
            .ToList() ?? new List<double>();

        while (result.Count < actualCount)
            result.Add(Math.Max(0.5, total / actualCount));

        var sum = result.Sum();
        if (sum <= 0)
            return Enumerable.Repeat(Math.Max(0.5, total / actualCount), actualCount).ToList();

        var ratio = total / sum;
        return result.Select(x => x * ratio).ToList();
    }

    private sealed record EditorState(LabelTemplate Template, int[] SelectedIndices);

    private sealed record TableBand(double Start, double End);

    private sealed record TableResizeTag(LabelElement Owner, int Index, bool IsColumn, int SegmentIndex);

    private FrameworkElement CreateImageElement(LabelElement item)
    {
        var rawPath = string.IsNullOrWhiteSpace(item.ImagePath) ? item.ContentTemplate : item.ImagePath;
        var resolvedPath = _fieldValues?.Resolve(rawPath) ?? TemplateEngine.Resolve(rawPath, _title, _partNumber,
            _backupField1, _backupField2, _sequenceCode, _productionDate, _operatorName);
        var path = ResolveImagePath(resolvedPath);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var cacheKey = $"{path}\u001f{File.GetLastWriteTimeUtc(path).Ticks}";
                if (!_imagePreviewCache.TryGetValue(cacheKey, out var bitmap))
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _imagePreviewCache[cacheKey] = bitmap;
                }

                return new Image { Source = bitmap, Stretch = Stretch.Uniform };
            }
            catch
            {
            }
        }

        return new TextBlock
        {
            Text = "Image",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.Gray
        };
    }

    private static string ResolveImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return string.Empty;

        var trimmed = imagePath.Trim();
        if (Path.IsPathRooted(trimmed))
            return trimmed;

        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, trimmed),
            Path.Combine(Environment.CurrentDirectory, trimmed),
            Path.Combine(AppContext.BaseDirectory, trimmed)
        };

        return candidates.FirstOrDefault(File.Exists) ?? trimmed;
    }

    private static string? SelectImagePath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Title = "选择打印图片",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
