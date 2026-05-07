# Revit 2026 API - Core Development Patterns

Quick reference for the most commonly used API patterns in Revit plugin development. All code based on Revit 2026 API (RevitAPI.dll v26.0.4.0).

## 1. ExternalCommand Entry Point

The most basic plugin entry point, triggered on each button click.

```csharp
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

[Transaction(TransactionMode.Manual)]
public class MyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiApp = commandData.Application;
        UIDocument uiDoc = uiApp.ActiveUIDocument;
        Document doc = uiDoc.Document;

        // Business logic...

        return Result.Succeeded;
    }
}
```

**Key points**:
- `TransactionMode.Manual` → Requires manual transaction management
- `TransactionMode.ReadOnly` → Read-only operation, no transaction needed
- Return `Result.Failed` and set `message` parameter for error display

## 2. ExternalApplication Entry Point

Application-level entry, loaded once at Revit startup, used to register Ribbon UI.

```csharp
public class MyApp : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        // Create Ribbon Tab and buttons
        application.CreateRibbonTab("MyTab");
        var panel = application.CreateRibbonPanel("MyTab", "MyPanel");

        var buttonData = new PushButtonData("cmdId", "Button Name",
            Assembly.GetExecutingAssembly().Location,
            "Namespace.MyCommand");  // Full qualified name of IExternalCommand

        panel.AddItem(buttonData);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        return Result.Succeeded;
    }
}
```

## 3. Transaction Management

All operations that modify the Revit model must execute within a transaction.

```csharp
// Basic transaction
using (Transaction tx = new Transaction(doc, "Transaction Description"))
{
    tx.Start();
    // Modification operations...
    tx.Commit();
}

// Transaction Group (multi-step undo merged into one)
using (TransactionGroup tg = new TransactionGroup(doc, "Group Description"))
{
    tg.Start();

    using (Transaction tx1 = new Transaction(doc, "Step 1"))
    {
        tx1.Start();
        // ...
        tx1.Commit();
    }

    using (Transaction tx2 = new Transaction(doc, "Step 2"))
    {
        tx2.Start();
        // ...
        tx2.Commit();
    }

    tg.Assimilate(); // Merge into single undo
}

// SubTransaction (does not create undo record)
using (Transaction tx = new Transaction(doc, "Main Transaction"))
{
    tx.Start();

    using (SubTransaction st = new SubTransaction(doc))
    {
        st.Start();
        // Temporary modifications...
        st.RollBack(); // or st.Commit()
    }

    tx.Commit();
}
```

## 4. FilteredElementCollector Element Query

Core API for querying elements in the model, supports chained filtering.

```csharp
// Query all walls
var walls = new FilteredElementCollector(doc)
    .OfClass(typeof(Wall))
    .ToElements();

// Query family instances of specific category
var doors = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Doors)
    .OfClass(typeof(FamilyInstance))
    .ToElements();

// Query in specific view
var viewElements = new FilteredElementCollector(doc, viewId)
    .OfCategory(BuiltInCategory.OST_Walls)
    .WhereElementIsNotElementType()
    .ToElements();

// Query types (not instances)
var wallTypes = new FilteredElementCollector(doc)
    .OfClass(typeof(WallType))
    .Cast<WallType>()
    .ToList();

// Combined filter (AND)
var filter = new LogicalAndFilter(
    new ElementCategoryFilter(BuiltInCategory.OST_Walls),
    new ElementIsElementTypeFilter(true) // true = not type
);
var result = new FilteredElementCollector(doc)
    .WherePasses(filter)
    .ToElements();

// Parameter filter
var paramFilter = new ElementParameterFilter(
    ParameterFilterRuleFactory.CreateEqualsRule(
        new ElementId(BuiltInParameter.WALL_BASE_CONSTRAINT),
        levelId));
var filteredWalls = new FilteredElementCollector(doc)
    .OfClass(typeof(Wall))
    .WherePasses(paramFilter)
    .ToElements();
```

**Performance tips**:
- `OfClass` and `OfCategory` are fast filters, use first
- `WhereElementIsNotElementType()` excludes type definitions
- Prefer `FirstElement()` or `FirstElementId()` over full iteration
- Use `ElementQuickFilter` (e.g., `BoundingBoxIntersectsFilter`) over `ElementSlowFilter`

## 5. Parameter Read/Write

```csharp
// Read by BuiltInParameter
Parameter p = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
double offset = p.AsDouble(); // Internal units (feet)

// Read by name (shared or project parameter)
Parameter nameParam = element.LookupParameter("Parameter Name");

// Read by GUID (shared parameter)
Parameter sharedParam = element.get_Parameter(new Guid("xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"));

// Write parameter (requires transaction)
p.Set(1.5); // Set to 1.5 feet

// Type parameter vs instance parameter
ElementId typeId = element.GetTypeId();
ElementType elemType = doc.GetElement(typeId) as ElementType;
Parameter typeParam = elemType.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);

// Unit conversion (Revit 2026 uses ForgeTypeId)
double meters = UnitUtils.ConvertFromInternalUnits(feetValue, UnitTypeId.Meters);
double feet = UnitUtils.ConvertToInternalUnits(meterValue, UnitTypeId.Meters);
```

## 6. Element Creation

```csharp
// Create wall
Wall wall = Wall.Create(doc, curve, wallTypeId, levelId, height, offset, false, false);

// Create floor (via CurveLoop)
var curveLoop = new CurveLoop();
curveLoop.Append(Line.CreateBound(p1, p2));
curveLoop.Append(Line.CreateBound(p2, p3));
curveLoop.Append(Line.CreateBound(p3, p4));
curveLoop.Append(Line.CreateBound(p4, p1));
Floor floor = Floor.Create(doc, new List<CurveLoop> { curveLoop }, floorTypeId, levelId);

// Place family instance
FamilyInstance inst = doc.Create.NewFamilyInstance(
    location,          // XYZ point
    familySymbol,      // FamilySymbol (must Activate first)
    level,             // Level
    StructuralType.NonStructural);

// Activate FamilySymbol (required before first placement)
if (!familySymbol.IsActive)
    familySymbol.Activate();
```

## 7. Selection User Interaction

```csharp
UIDocument uiDoc = commandData.Application.ActiveUIDocument;

// Select single element
Reference picked = uiDoc.Selection.PickObject(ObjectType.Element, "Please select element");
Element elem = doc.GetElement(picked);

// Select multiple elements
IList<Reference> refs = uiDoc.Selection.PickObjects(ObjectType.Element, "Please select multiple elements");

// Selection with filter
IList<Reference> wallRefs = uiDoc.Selection.PickObjects(
    ObjectType.Element,
    new WallSelectionFilter(),  // Custom ISelectionFilter
    "Please select walls");

// Pick point
XYZ point = uiDoc.Selection.PickPoint("Please pick a point");

// Get current selection
ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();

// ISelectionFilter implementation
public class WallSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) => elem is Wall;
    public bool AllowReference(Reference reference, XYZ position) => false;
}
```

## 8. Event Subscription

```csharp
// Register in IExternalApplication.OnStartup
public Result OnStartup(UIControlledApplication app)
{
    // DB-level events
    app.ControlledApplication.DocumentOpened += OnDocumentOpened;
    app.ControlledApplication.DocumentChanged += OnDocumentChanged;

    // UI-level events
    app.ViewActivated += OnViewActivated;
    app.Idling += OnIdling;

    return Result.Succeeded;
}

private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
{
    Document doc = e.GetDocument();
    ICollection<ElementId> added = e.GetAddedElementIds();
    ICollection<ElementId> deleted = e.GetDeletedElementIds();
    ICollection<ElementId> modified = e.GetModifiedElementIds();
}

// Unregister in OnShutdown
public Result OnShutdown(UIControlledApplication app)
{
    app.ControlledApplication.DocumentOpened -= OnDocumentOpened;
    return Result.Succeeded;
}
```

## 9. Geometry Operations

```csharp
// Get element geometry
Options geoOptions = new Options
{
    ComputeReferences = true,
    DetailLevel = ViewDetailLevel.Fine,
    IncludeNonVisibleObjects = false
};
GeometryElement geoElem = element.get_Geometry(geoOptions);

// Iterate geometry
foreach (GeometryObject geoObj in geoElem)
{
    if (geoObj is Solid solid && solid.Volume > 0)
    {
        foreach (Face face in solid.Faces)
        {
            // Process face...
            double area = face.Area;
        }
        foreach (Edge edge in solid.Edges)
        {
            Curve curve = edge.AsCurve();
        }
    }
    else if (geoObj is GeometryInstance geoInst)
    {
        // Family instance → needs recursion
        GeometryElement instGeo = geoInst.GetInstanceGeometry();
    }
}

// Create geometry
XYZ p1 = new XYZ(0, 0, 0);
XYZ p2 = new XYZ(10, 0, 0);
Line line = Line.CreateBound(p1, p2);
Arc arc = Arc.Create(p1, p2, midPoint);

// BoundingBox
BoundingBoxXYZ bb = element.get_BoundingBox(null);
XYZ min = bb.Min;
XYZ max = bb.Max;
```

## 10. ExtensibleStorage Extended Storage

Store custom data on elements (does not affect the model).

```csharp
// Define Schema
Guid schemaGuid = new Guid("xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx");
SchemaBuilder sb = new SchemaBuilder(schemaGuid);
sb.SetSchemaName("MyData");
sb.SetReadAccessLevel(AccessLevel.Public);
sb.SetWriteAccessLevel(AccessLevel.Vendor);
sb.SetVendorId("YOUR_VENDOR_ID");

FieldBuilder fb = sb.AddSimpleField("Value", typeof(string));
fb.SetDocumentation("Stored data");

Schema schema = sb.Finish();

// Write
Entity entity = new Entity(schema);
entity.Set<string>("Value", "hello");
element.SetEntity(entity);

// Read
Entity readEntity = element.GetEntity(schema);
if (readEntity.IsValid())
{
    string value = readEntity.Get<string>("Value");
}
```

## 11. .addin Registration File

```xml
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <!-- ExternalCommand -->
  <AddIn Type="Command">
    <Assembly>path\to\MyPlugin.dll</Assembly>
    <FullClassName>Namespace.MyCommand</FullClassName>
    <ClientId>GUID-HERE</ClientId>
    <Text>Command Name</Text>
    <VendorId>YOUR_VENDOR</VendorId>
  </AddIn>

  <!-- ExternalApplication -->
  <AddIn Type="Application">
    <Assembly>path\to\MyPlugin.dll</Assembly>
    <FullClassName>Namespace.MyApp</FullClassName>
    <ClientId>GUID-HERE</ClientId>
    <Name>Application Name</Name>
    <VendorId>YOUR_VENDOR</VendorId>
  </AddIn>
</RevitAddIns>
```

**Addins directory**: `C:\ProgramData\Autodesk\Revit\Addins\2026\`
