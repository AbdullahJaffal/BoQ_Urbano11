using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.Behaviors
{
    /// <summary>
    /// Shared multi-row behavior for every catalog / settings DataGrid ("toplu işlem"):
    /// <list type="bullet">
    /// <item>Extended selection (Ctrl / Shift / drag).</item>
    /// <item>One right-click menu for all grids: Yeni Ekle · Çoğalt · Sil · Tümünü Seç. Çoğalt and Sil
    ///   run on EVERY selected row (the VM command is executed once per row, with that row made the
    ///   grid's SelectedItem and passed as the command parameter). No confirmation prompt.</item>
    /// <item>Del key = Sil (when not editing a cell). Esc = collapse the selection to the current row.</item>
    /// <item>Toplu düzenleme: while several rows are selected, clicking a cell of one of them edits it
    ///   WITHOUT dropping the selection; on commit the edited property's value is copied to every
    ///   other selected row. The property is taken from the column binding (text / check-box /
    ///   combo columns) or, for template columns, from the editing control's binding — override
    ///   per column with <c>GridBulk.Path</c> ("" / "-" disables bulk copy for that column).</item>
    /// </list>
    /// Usage: <c>beh:GridBulk.Enable="True" beh:GridBulk.DeleteCommand="{Binding DeleteCommand}"
    /// beh:GridBulk.DuplicateCommand="{Binding DuplicateCommand}" beh:GridBulk.AddCommand="{Binding AddCommand}"</c>.
    /// </summary>
    public static class GridBulk
    {
        // ── Attached properties ──────────────────────────────────────────────

        public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
            "Enable", typeof(bool), typeof(GridBulk), new PropertyMetadata(false, OnEnableChanged));
        public static bool GetEnable(DependencyObject o) => (bool)o.GetValue(EnableProperty);
        public static void SetEnable(DependencyObject o, bool v) => o.SetValue(EnableProperty, v);

        public static readonly DependencyProperty AddCommandProperty = DependencyProperty.RegisterAttached(
            "AddCommand", typeof(ICommand), typeof(GridBulk), new PropertyMetadata(null));
        public static ICommand GetAddCommand(DependencyObject o) => (ICommand)o.GetValue(AddCommandProperty);
        public static void SetAddCommand(DependencyObject o, ICommand v) => o.SetValue(AddCommandProperty, v);

        public static readonly DependencyProperty DuplicateCommandProperty = DependencyProperty.RegisterAttached(
            "DuplicateCommand", typeof(ICommand), typeof(GridBulk), new PropertyMetadata(null));
        public static ICommand GetDuplicateCommand(DependencyObject o) => (ICommand)o.GetValue(DuplicateCommandProperty);
        public static void SetDuplicateCommand(DependencyObject o, ICommand v) => o.SetValue(DuplicateCommandProperty, v);

        public static readonly DependencyProperty DeleteCommandProperty = DependencyProperty.RegisterAttached(
            "DeleteCommand", typeof(ICommand), typeof(GridBulk), new PropertyMetadata(null));
        public static ICommand GetDeleteCommand(DependencyObject o) => (ICommand)o.GetValue(DeleteCommandProperty);
        public static void SetDeleteCommand(DependencyObject o, ICommand v) => o.SetValue(DeleteCommandProperty, v);

        /// <summary>Column-level override of the property copied by bulk edit ("" or "-" = no bulk copy).</summary>
        public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
            "Path", typeof(string), typeof(GridBulk), new PropertyMetadata(null));
        public static string GetPath(DependencyObject o) => (string)o.GetValue(PathProperty);
        public static void SetPath(DependencyObject o, string v) => o.SetValue(PathProperty, v);

        // ── Wiring ───────────────────────────────────────────────────────────

        private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is DataGrid grid)) return;
            grid.PreviewMouseRightButtonDown -= OnPreviewRightDown;
            grid.PreviewMouseLeftButtonDown  -= OnPreviewLeftDown;
            grid.BeginningEdit               -= OnBeginningEdit;
            grid.CellEditEnding              -= OnCellEditEnding;
            grid.PreviewKeyDown              -= OnPreviewKeyDown;
            grid.RemoveHandler(Selector.SelectionChangedEvent, (SelectionChangedEventHandler)OnEditorSelectionChanged);
            if (!(bool)e.NewValue) return;

            grid.SelectionMode     = DataGridSelectionMode.Extended;
            grid.SelectionUnit     = DataGridSelectionUnit.FullRow;
            grid.CanUserDeleteRows = false;   // built-in Del would bypass the VM (model left stale)

            grid.PreviewMouseRightButtonDown += OnPreviewRightDown;
            grid.PreviewMouseLeftButtonDown  += OnPreviewLeftDown;
            grid.BeginningEdit               += OnBeginningEdit;
            grid.CellEditEnding              += OnCellEditEnding;
            grid.PreviewKeyDown              += OnPreviewKeyDown;
            grid.AddHandler(Selector.SelectionChangedEvent, (SelectionChangedEventHandler)OnEditorSelectionChanged);

            var menu = new ContextMenu();
            menu.Opened += (s, a) => FillMenu(grid, menu);
            grid.ContextMenu = menu;
        }

        // ── Context menu ─────────────────────────────────────────────────────

        private static void FillMenu(DataGrid grid, ContextMenu menu)
        {
            menu.Items.Clear();
            var sel = SelectedRealItems(grid);
            int n = sel.Count;
            string suffix = n > 1 ? " (" + n + " satır)" : "";

            var add = GetAddCommand(grid);
            if (add != null)
                menu.Items.Add(MakeItem("Yeni Ekle", null, SafeCanExecute(add, null),
                    () => { grid.CommitEdit(DataGridEditingUnit.Row, true); add.Execute(null); }));

            var dup = GetDuplicateCommand(grid);
            if (dup != null)
                menu.Items.Add(MakeItem("Çoğalt" + suffix, null, n > 0 && SafeCanExecute(dup, sel[0]),
                    () => RunPerItem(grid, dup)));

            var del = GetDeleteCommand(grid);
            if (del != null)
                menu.Items.Add(MakeItem("Sil" + suffix, "Del", n > 0 && SafeCanExecute(del, sel[0]),
                    () => RunPerItem(grid, del)));

            if (grid.Items.Count > 0)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                menu.Items.Add(MakeItem("Tümünü Seç", "Ctrl+A", true, () => grid.SelectAll()));
            }

            if (menu.Items.Count == 0) menu.IsOpen = false;
        }

        private static MenuItem MakeItem(string header, string gesture, bool enabled, Action onClick)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture != null) mi.InputGestureText = gesture;
            mi.Click += (s, e) => onClick();
            return mi;
        }

        private static bool SafeCanExecute(ICommand cmd, object p)
        {
            try { return cmd.CanExecute(p); } catch { return false; }
        }

        /// <summary>
        /// Runs a row-scoped VM command once per selected row: the row becomes the grid's (and so the
        /// VM's) SelectedItem and is also passed as the parameter, so both selection-based and
        /// parameter-based commands work unchanged.
        /// </summary>
        private static void RunPerItem(DataGrid grid, ICommand cmd)
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            var items = SelectedRealItems(grid);
            foreach (var it in items)
            {
                if (!grid.Items.Contains(it)) continue;   // already gone (cascade / rebuild)
                grid.SelectedItem = it;
                if (SafeCanExecute(cmd, it)) cmd.Execute(it);
            }
            CommandManager.InvalidateRequerySuggested();
        }

        private static List<object> SelectedRealItems(DataGrid grid)
            => grid.SelectedItems.Cast<object>()
                   .Where(o => o != null && o != CollectionView.NewItemPlaceholder)
                   .ToList();

        // ── Mouse / keyboard ─────────────────────────────────────────────────

        /// <summary>Right-click selects the clicked row unless it is already part of the selection
        /// (so a multi-row selection survives opening the menu).</summary>
        private static void OnPreviewRightDown(object sender, MouseButtonEventArgs e)
        {
            var grid = (DataGrid)sender;
            var row  = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null || row.IsSelected) return;
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            grid.SelectedItem = row.Item;
        }

        /// <summary>While several rows are selected, a plain click on a cell of a selected row starts
        /// editing that cell instead of collapsing the selection (bulk edit entry point).</summary>
        private static void OnPreviewLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            var grid = (DataGrid)sender;
            if (grid.SelectedItems.Count < 2) return;

            var src  = e.OriginalSource as DependencyObject;
            var cell = FindAncestor<DataGridCell>(src);
            if (cell == null || cell.IsEditing) return;
            var row = FindAncestor<DataGridRow>(cell);
            if (row == null || !row.IsSelected) return;
            if (IsInsideLiveControl(src, cell)) return;   // in-template editor: let it handle the click

            e.Handled = true;
            grid.CurrentCell = new DataGridCellInfo(row.Item, cell.Column);
            cell.Focus();
            if (cell.IsReadOnly || grid.IsReadOnly) return;
            grid.BeginEdit(e);

            // A check-box toggles on this very click — apply it to the selection right away.
            if (cell.Column is DataGridCheckBoxColumn)
                grid.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    new Action(() => grid.CommitEdit(DataGridEditingUnit.Cell, true)));
        }

        /// <summary>During a bulk edit, picking an item from a cell's drop-down commits at once, so the
        /// choice reaches every selected row without having to leave the cell first.</summary>
        private static void OnEditorSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var grid = (DataGrid)sender;
            if (!(e.OriginalSource is ComboBox combo) || !combo.IsDropDownOpen) return;
            if (!(grid.GetValue(EditTargetsProperty) is List<object> t) || t.Count == 0) return;
            if (FindAncestor<DataGridCell>(combo)?.IsEditing != true) return;
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() => grid.CommitEdit(DataGridEditingUnit.Cell, true)));
        }

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var grid = (DataGrid)sender;
            bool notEditing = e.OriginalSource is DataGridCell || e.OriginalSource is DataGrid || e.OriginalSource is DataGridRow;
            if (!notEditing) return;

            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                var del = GetDeleteCommand(grid);
                if (del == null || grid.SelectedItems.Count == 0) return;
                e.Handled = true;
                RunPerItem(grid, del);
            }
            else if (e.Key == Key.Escape && grid.SelectedItems.Count > 1)
            {
                var cur = grid.CurrentItem;
                if (cur == null || !grid.SelectedItems.Contains(cur)) cur = grid.SelectedItems[0];
                grid.SelectedItem = cur;
                e.Handled = true;
            }
        }

        private static bool IsInsideLiveControl(DependencyObject src, DataGridCell cell)
        {
            for (var d = src; d != null && d != cell; d = ParentOf(d))
                if (d is ButtonBase || d is TextBoxBase || d is Selector) return true;
            return false;
        }

        // ── Bulk edit ────────────────────────────────────────────────────────

        // Selection captured when a cell edit starts: the commit can be triggered by a click that
        // already moves the selection, so the fan-out targets must not be read at commit time.
        private static readonly DependencyProperty EditTargetsProperty = DependencyProperty.RegisterAttached(
            "EditTargets", typeof(List<object>), typeof(GridBulk), new PropertyMetadata(null));

        private static void OnBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            var grid = (DataGrid)sender;
            object edited = e.Row?.Item;
            List<object> targets = null;
            if (edited != null && grid.SelectedItems.Count > 1 && grid.SelectedItems.Contains(edited))
                targets = SelectedRealItems(grid).Where(o => !ReferenceEquals(o, edited)).ToList();
            grid.SetValue(EditTargetsProperty, targets);
        }

        private static void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            var grid = (DataGrid)sender;
            var targets = grid.GetValue(EditTargetsProperty) as List<object>;
            grid.SetValue(EditTargetsProperty, null);
            if (e.EditAction != DataGridEditAction.Commit || targets == null || targets.Count == 0) return;

            object edited = e.Row?.Item;
            if (edited == null) return;

            var spec = ResolveSpec(e.Column, e.EditingElement, edited);
            if (spec == null) return;

            // A binding with the default (LostFocus/Explicit) trigger may keep the typed value in the
            // row's BindingGroup until the whole row commits — push it now so it can be fanned out.
            try { spec.Editor?.UpdateSource(); } catch { /* validation failure → nothing to copy */ }

            // Let the cell's binding push its value to the source first, then fan it out.
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!TryGetValue(edited, spec.Path, out var value)) return;
                foreach (var t in targets)
                    if (grid.Items.Contains(t) && spec.IsValidFor(t, value)) TrySetValue(t, spec.Path, value);
            }));
        }

        /// <summary>What a bulk edit copies: the property path, plus — for a combo whose list is
        /// per-row (ItemsSource bound to the row item) — how to check the value exists in each
        /// target row's own list (e.g. a pipe of another family is not copied to that row).</summary>
        private sealed class EditSpec
        {
            public string Path;
            public BindingExpressionBase Editor;   // the editing control's binding (pushed before fan-out)
            public string ItemsPath;          // row-relative ItemsSource path, or null (shared list)
            public string ValuePath;          // SelectedValuePath when the combo binds SelectedValue
            public bool   BySelectedValue;

            public bool IsValidFor(object target, object value)
            {
                if (ItemsPath == null) return true;
                if (!TryGetValue(target, ItemsPath, out var itemsObj) || !(itemsObj is System.Collections.IEnumerable items))
                    return false;
                foreach (var it in items)
                {
                    object v = it;
                    if (BySelectedValue && !string.IsNullOrEmpty(ValuePath) && !TryGetValue(it, ValuePath, out v)) continue;
                    if (Equals(v, value)) return true;
                }
                return false;
            }
        }

        private static EditSpec ResolveSpec(DataGridColumn column, FrameworkElement editingElement, object item)
        {
            string over = GetPath(column);
            if (over != null) return (over == "-" || over.Length == 0) ? null : new EditSpec { Path = over };

            string path = null;
            if (column is DataGridComboBoxColumn cc)
                path = PathOf(cc.SelectedItemBinding) ?? PathOf(cc.SelectedValueBinding) ?? PathOf(cc.TextBinding);
            else if (column is DataGridBoundColumn bc)
                path = PathOf(bc.Binding);
            if (path != null)
            {
                BindingExpressionBase be = null;
                if (editingElement is TextBox)          be = BindingOperations.GetBindingExpressionBase(editingElement, TextBox.TextProperty);
                else if (editingElement is ToggleButton) be = BindingOperations.GetBindingExpressionBase(editingElement, ToggleButton.IsCheckedProperty);
                return new EditSpec { Path = path, Editor = be };
            }

            // Template column: find the first visible, enabled editor bound to the row item.
            return FindEditorSpec(editingElement, item);
        }

        private static string PathOf(BindingBase b)
        {
            if (!(b is Binding bb) || bb.Path == null) return null;
            if (bb.Source != null || bb.RelativeSource != null || !string.IsNullOrEmpty(bb.ElementName)) return null;
            return string.IsNullOrEmpty(bb.Path.Path) || bb.Path.Path == "." ? null : bb.Path.Path;
        }

        private static EditSpec FindEditorSpec(DependencyObject root, object item)
        {
            if (root == null) return null;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var d = queue.Dequeue();
                if (d is FrameworkElement fe && ReferenceEquals(fe.DataContext, item) &&
                    fe.Visibility == Visibility.Visible && fe.IsEnabled)
                {
                    if (d is Selector sel)
                    {
                        var p = PathOf(BindingOperations.GetBinding(d, Selector.SelectedItemProperty));
                        bool byValue = false;
                        var dp = Selector.SelectedItemProperty;
                        if (p == null) { dp = Selector.SelectedValueProperty; p = PathOf(BindingOperations.GetBinding(d, dp)); byValue = p != null; }
                        if (p == null && d is ComboBox) { dp = ComboBox.TextProperty; p = PathOf(BindingOperations.GetBinding(d, dp)); }
                        if (p != null)
                            return new EditSpec
                            {
                                Path            = p,
                                Editor          = BindingOperations.GetBindingExpressionBase(d, dp),
                                ItemsPath       = PathOf(BindingOperations.GetBinding(d, ItemsControl.ItemsSourceProperty)),
                                ValuePath       = sel.SelectedValuePath,
                                BySelectedValue = byValue
                            };
                    }
                    else if (d is ToggleButton || d is TextBox)
                    {
                        var dp = d is ToggleButton ? ToggleButton.IsCheckedProperty : TextBox.TextProperty;
                        var p  = PathOf(BindingOperations.GetBinding(d, dp));
                        if (p != null) return new EditSpec { Path = p, Editor = BindingOperations.GetBindingExpressionBase(d, dp) };
                    }
                }
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
            return null;
        }

        private static bool TryGetValue(object obj, string path, out object value)
        {
            value = null;
            var parts = path.Split('.');
            object cur = obj;
            foreach (var part in parts)
            {
                if (cur == null) return false;
                var pi = cur.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
                if (pi == null || !pi.CanRead || pi.GetIndexParameters().Length > 0) return false;
                cur = pi.GetValue(cur, null);
            }
            value = cur;
            return true;
        }

        private static void TrySetValue(object obj, string path, object value)
        {
            try
            {
                var parts = path.Split('.');
                object cur = obj;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var pi = cur?.GetType().GetProperty(parts[i], BindingFlags.Public | BindingFlags.Instance);
                    if (pi == null) return;
                    cur = pi.GetValue(cur, null);
                }
                if (cur == null) return;
                var last = cur.GetType().GetProperty(parts[parts.Length - 1], BindingFlags.Public | BindingFlags.Instance);
                if (last == null || !last.CanWrite || last.GetSetMethod() == null) return;
                if (value != null && !last.PropertyType.IsInstanceOfType(value)) return;
                if (value == null && last.PropertyType.IsValueType && Nullable.GetUnderlyingType(last.PropertyType) == null) return;
                last.SetValue(cur, value, null);
            }
            catch
            {
                // A row that rejects the value (validation / different row type) is simply skipped.
            }
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T)) d = ParentOf(d);
            return d as T;
        }

        /// <summary>Visual parent, or the logical parent for content elements (e.g. a Run inside a
        /// TextBlock) where VisualTreeHelper.GetParent would throw.</summary>
        private static DependencyObject ParentOf(DependencyObject d)
            => (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
    }
}
