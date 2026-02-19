using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace TNDexplorer.Converters
{
    /// <summary>
    /// Attached property that allows binding a collection of Inlines to a TextBlock.
    /// Usage: converters:BindableInlines.InlineList="{Binding ...}"
    /// </summary>
    public static class BindableInlines
    {
        public static readonly DependencyProperty InlineListProperty =
            DependencyProperty.RegisterAttached(
                "InlineList",
                typeof(IEnumerable<Inline>),
                typeof(BindableInlines),
                new PropertyMetadata(null, OnInlineListChanged));

        public static IEnumerable<Inline> GetInlineList(DependencyObject obj)
        {
            return (IEnumerable<Inline>)obj.GetValue(InlineListProperty);
        }

        public static void SetInlineList(DependencyObject obj, IEnumerable<Inline> value)
        {
            obj.SetValue(InlineListProperty, value);
        }

        private static void OnInlineListChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock textBlock)
            {
                textBlock.Inlines.Clear();
                var inlines = e.NewValue as IEnumerable<Inline>;
                if (inlines != null)
                {
                    foreach (var inline in inlines)
                    {
                        textBlock.Inlines.Add(inline);
                    }
                }
            }
        }
    }
}
