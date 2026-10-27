using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Xml;
using System.Xml.Linq;

namespace ToolkitLauncher.Acceptance;

internal static class ProductionResources
{
    public static ResourceDictionary Load()
    {
        // Embed the unchanged production source directly; no copied/reimplemented styles.
        // Extract only Application.Resources so no production Application or startup executes.
        using var source = typeof(ProductionResources).Assembly.GetManifestResourceStream("ToolkitAcceptance.ProductionApp.xaml")
            ?? throw new InvalidDataException("Embedded production App.xaml is missing.");
        using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var application = document.Root ?? throw new InvalidDataException("Production App.xaml has no root.");
        var resources = application.Element(presentation + "Application.Resources")
            ?? throw new InvalidDataException("Production Application.Resources is missing.");
        if (application.Name != presentation + "Application" || application.Elements().Count() != 1)
            throw new InvalidDataException("Production application resource shape changed; review the QA loader.");
        var dictionary = new XElement(presentation + "ResourceDictionary",
            application.Attributes().Where(a => a.IsNamespaceDeclaration), resources.Nodes());
        using var resourceReader = dictionary.CreateReader();
        return XamlReader.Load(resourceReader) as ResourceDictionary
            ?? throw new InvalidDataException("Production resources did not create a ResourceDictionary.");
    }
}
