# Revit 2026 API - Namespace Overview

30 namespaces, 2724 types.

## Autodesk.Revit.ApplicationServices

**Type count**: 4

| Type | Kind | Description |
|------|------|------|
| Application | Class | Represents the Autodesk Revit Application, providing access to documents, opt... |
| ControlledApplication | Class | Represents the Autodesk Revit Application with no access to documents. It pro... |
| LanguageType | Enumeration | An enumerated type containing the supported Revit product languages. |
| ProductType | Enumeration | An enumerated type containing the possible Revit product types. |

## Autodesk.Revit.Attributes

**Type count**: 6

| Type | Kind | Description |
|------|------|------|
| JournalingAttribute | Class | The custom journaling attribute to control the journaling behavior of the ext... |
| JournalingMode | Enumeration | All journaling modes supported by Revit external commands. |
| RegenerationAttribute | Class | The custom regeneration attribute to control the regeneration behavior of the... |
| RegenerationOption | Enumeration | All regeneration options supported by Revit external commands and external ap... |
| TransactionAttribute | Class | The custom transaction attribute to control the transaction behavior of the e... |
| TransactionMode | Enumeration | All transaction modes supported by Revit external commands. |

## Autodesk.Revit.Creation

**Type count**: 7

| Type | Kind | Description |
|------|------|------|
| Application | Class | The Application Creation object is used to create new instances of utility ob... |
| AreaCreationData | Class | A class which wraps the arguments of Area for batch creation |
| Document | Class | The Document Creation object is used to create new instances of elements with... |
| FamilyInstanceCreationData | Class | A class which wraps the arguments of FamilyInstance for batch creation. |
| FamilyItemFactory | Class | The Family Item Factory object is used to create new instances of elements wi... |
| ItemFactoryBase | Class | The ItemFactoryBase object is used to create new instances of elements within... |
| eRefFace | Enumeration | Indicates the reference face. The Opening will be created at the direction pe... |

## Autodesk.Revit.DB

**Type count**: 1472

| Type | Kind | Description |
|------|------|------|
| ACADExportOptions | Class | The base class for options used to export DWG and DXF format files. |
| ACADVersion | Enumeration | An enumerated type listing available AutoCAD versions, into which a file may ... |
| ACAObjectPreference | Enumeration | An enumerated type listing possible ways to generate geometry of an ACA objec... |
| APIObject | Class | Supports all objects in the Autodesk Revit API hierarchy. |
| AXMImportOptions | Class | The import options used to import AXM format files. |
| AdaptiveComponentFamilyUtils | Class | An interface for Adaptive Component Instances. |
| AdaptiveComponentInstanceUtils | Class | An interface for Adaptive Component Instances. |
| AdaptivePointConstraintType | Enumeration | An enumerated type containing possible constraint types for Adaptive Points. |
| AdaptivePointOrientationType | Enumeration | An enumerated type containing possible orientation types for Adaptive Points. |
| AdaptivePointType | Enumeration | An enumerated type containing possible types for Adaptive Points. |
| AddInId | Class | Identifies an AddIn registered with Revit |
| AllowedValues | Enumeration | A range of allowed values. |
| AlphanumericRevisionSettings | Class | Contains settings that apply to Revisions with the Alphanumeric RevisionNumbe... |
| AlternateUnits | Enumeration | An enumerated type listing the locations where Alternate units may be display... |
| AnalyzesAsType | Enumeration | This enum class is used for the BuiltInParameter STRUCTURAL_ANALYZES_AS. |
| AngularDimension | Class | An object that represents an Angular Dimension within the Revit project. |
| AnnotationMultipleAlignmentUtils | Class | A helper providing functionality related to elements that can be aligned to o... |
| AnnotationSymbol | Class | This object represents a symbol of the Generic Annotation. |
| AnnotationSymbolType | Class | An object that represents an annotation style. |
| AppearanceAssetElement | Class | An element that contains a rendering asset used as a portion of a material de... |
| ... | | *1452 more types, use search_api.py to query* |

## Autodesk.Revit.DB.Analysis

**Type count**: 103

| Type | Kind | Description |
|------|------|------|
| AllowLargeGeometry | Enumeration | An enumerated type containing settings information related to handling of lar... |
| AnalysisDisplayColorEntry | Class | Contains one entry of intermediate colors in color settings for analysis disp... |
| AnalysisDisplayColorSettings | Class | Contains color settings for analysis display style element. |
| AnalysisDisplayColoredSurfaceSettings | Class | Contains colored surface settings for analysis display style element. |
| AnalysisDisplayDeformedShapeSettings | Class | Contains deformed shape settings for analysis display style element. |
| AnalysisDisplayDiagramSettings | Class | Contains diagram settings for analysis display style element. |
| AnalysisDisplayLegend | Class | The legend that describes an Analysis Visualization. |
| AnalysisDisplayLegendSettings | Class | Contains legend settings for analysis display style element. |
| AnalysisDisplayMarkersAndTextSettings | Class | Contains markers and text settings for analysis display style element. |
| AnalysisDisplayStyle | Class | Exposes API for manipulation of analysis display style. |
| AnalysisDisplayStyleColorSettingsType | Enumeration | Defines types for color settings of analysis display style. |
| AnalysisDisplayStyleDeformedShapeTextLabelType | Enumeration | Defines text visualization types for deformed shape settings of analysis disp... |
| AnalysisDisplayStyleDiagramFenceType | Enumeration | Defines fence visualization types for diagram settings of analysis display st... |
| AnalysisDisplayStyleDiagramTextLabelType | Enumeration | Defines text label visualization types for diagram settings of analysis displ... |
| AnalysisDisplayStyleMarkerTextLabelType | Enumeration | Text label visualization types for Markers and Text settings of analysis disp... |
| AnalysisDisplayStyleMarkerType | Enumeration | Marker types for Markers and Text settings of analysis display style. |
| AnalysisDisplayStyleVectorArrowheadScale | Enumeration | Defines arrow head scaling for vector settings of analysis display style. |
| AnalysisDisplayStyleVectorOrientation | Enumeration | Defines vector orientation for vector settings of analysis display style. |
| AnalysisDisplayStyleVectorPosition | Enumeration | Defines vector position for vector settings of analysis display style. |
| AnalysisDisplayStyleVectorTextType | Enumeration | Defines text visualization types for vector settings of analysis display style. |
| ... | | *83 more types, use search_api.py to query* |

## Autodesk.Revit.DB.Architecture

**Type count**: 76

| Type | Kind | Description |
|------|------|------|
| BalusterInfo | Class | The class represents an instance of a railing baluster or post. |
| BalusterPattern | Class | Baluster pattern class. |
| BalusterPlacement | Class | A class which contains information regarding baluster and post placement for ... |
| BreakCornerCondition | Enumeration | Condition on which the corner post is inserted. |
| BreakPatternCondition | Enumeration | Condition on which patterns distribution will be broken. |
| BuildingPad | Class | Represents a BuildingPad element. |
| ContinuousRail | Class | Represents a continuous rail element in Autodesk Revit. |
| ContinuousRailType | Class | A type element containing the properties of a continuous rail. |
| CutLineType | Enumeration | The available line types for a stairs cut line. |
| CutMarkSymbol | Enumeration | The available shapes for the cut mark symbol. |
| CutMarkType | Class | An object represents the cut mark type in Autodesk Revit. |
| Fascia | Class | An object that represents a fascia within the Autodesk Revit project. |
| FasciaType | Class | An object that represents the fascia type in Autodesk Revit. |
| Gutter | Class | An object that represents a gutter within the Autodesk Revit project. |
| GutterType | Class | An object that represents the gutter type in Autodesk Revit. |
| HandRail | Class | Represents a hand rail element in Autodesk Revit. |
| HandRailPosition | Enumeration | The position of the hand rail. |
| HandRailType | Class | A rail type object that is used in the generation of hand rail. |
| MultistoryStairs | Class | Represents a multistory stairs element in Autodesk Revit. |
| NonContinuousRailInfo | Class | A class which contains information needed to define a single non-continuous r... |
| ... | | *56 more types, use search_api.py to query* |

## Autodesk.Revit.DB.DirectContext3D

**Type count**: 32

| Type | Kind | Description |
|------|------|------|
| Camera | Class | A collection of camera settings for the current view. |
| ClipPlane | Class | A set of parameters representing a clip plane in DirectContext3D. |
| DirectContext3DDocumentUtils | Class | The methods provided by this utility class support the use of DirectContext3D... |
| DirectContext3DHandleOverrides | Class | A set of DirectContext3DHandleSettings that are stored by a view. |
| DirectContext3DHandleSettings | Class | Overriding settings applied to DirectContext3DHandles through the Visibility ... |
| DrawContext | Class | A class that provides drawing functionality for use by servers |
| EffectInstance | Class | An effect instance that controls the appearance of geometry. |
| IDirectContext3DServer | Interface | The interface to be implemented by a server of the DirectContext3D external s... |
| IndexBuffer | Class | A buffer that stores vertex indices for rendering. |
| IndexLine | Class | A line segment primitive consisting of two indices. |
| IndexPoint | Class | A point primitive consisting of one index. |
| IndexPrimitive | Class | The base class for index buffer primitives. |
| IndexStream | Class | The base class for DirectContext3D index streams, which are used to write ver... |
| IndexStreamLine | Class | A stream that can be used to write primitives into an |
| IndexStreamPoint | Class | A stream that can be used to write primitives into an |
| IndexStreamTriangle | Class | A stream that can be used to write primitives into an |
| IndexTriangle | Class | A triangle primitive consisting of three indices. |
| PrimitiveType | Enumeration | Type of geometry primitive represented as a number. |
| ProjectionMethod | Enumeration | Projection method |
| Vertex | Class | The base class for DirectContext3D vertices. |
| ... | | *12 more types, use search_api.py to query* |

## Autodesk.Revit.DB.Electrical

**Type count**: 112

| Type | Kind | Description |
|------|------|------|
| AnalyticalBusData | Class | Represents the data and parameters of analytical bus node. |
| AnalyticalDistributionNodePropertyData | Class | Represents the data and parameters of electrical analytical node. |
| AnalyticalEquipmentLoadData | Class | Represents the data and parameters of point load node. |
| AnalyticalPowerDistributableNodeData | Class | Represents the data and parameters of a power distributable node. A power dis... |
| AnalyticalPowerSourceData | Class | Represents the data and parameters of analytical power source node. |
| AnalyticalTransferSwitchData | Class | Represents the data and parameters of electrical analytical transfer switch. |
| AnalyticalTransformerData | Class | Represents the data and parameters of analytical transformer node. |
| AreaBasedLoadBoundaryLineData | Class | Wrapper class used to access area based load boundary line related data. |
| AreaBasedLoadData | Class | Represents the electrical area based load data. |
| AreaBasedLoadType | Class | This class represents an area based load type in Autodesk Revit. |
| CableSize | Class | This class represents the definition of Cable Size data. |
| CableTray | Class | This class represents a cable tray in Autodesk Revit. |
| CableTrayConduitBase | Class | The CableTrayConduitBase class is implemented as the base class for cable tra... |
| CableTrayConduitRunBase | Class | The base class for a cable tray or conduit run in Autodesk Revit. |
| CableTrayRun | Class | This class represents a cable tray run in Autodesk Revit. |
| CableTraySettings | Class | The cable tray settings. |
| CableTrayShape | Enumeration | Shape types enum of cable tray |
| CableTraySizeIterator | Class | An iterator to a set of MEP cable tray sizes from CableTraySizes. |
| CableTraySizes | Class | Cable tray sizes. |
| CableTrayType | Class | This class represents a cable tray type in Autodesk Revit. |
| ... | | *92 more types, use search_api.py to query* |

## Autodesk.Revit.DB.Events

**Type count**: 59

| Type | Kind | Description |
|------|------|------|
| ApplicationInitializedEventArgs | Class | The event arguments used by the ApplicationLaunched event. |
| CreateRelatedFileProgressChangedEventArgs | Class | The event arguments used during creating related file phase of in model open ... |
| DataTransferMode | Enumeration | Describes the data transfer mode. |
| DataTransferProgressChangedEventArgs | Class | The event arguments used during the data transferring phase of . |
| DocumentChangedEventArgs | Class | The event arguments used by the DocumentChanged event. |
| DocumentClosedEventArgs | Class | The event arguments used by the DocumentClosed event. |
| DocumentClosingEventArgs | Class | The event arguments used by the DocumentClosing event. |
| DocumentCreatedEventArgs | Class | The event arguments used by the DocumentCreated event. |
| DocumentCreatingEventArgs | Class | The event arguments used by the DocumentCreating event. |
| DocumentOpenedEventArgs | Class | The event arguments used by the DocumentOpened event. |
| DocumentOpeningEventArgs | Class | The event arguments used by the DocumentOpening event. |
| DocumentPrintedEventArgs | Class | The event arguments used by the DocumentPrinted event. |
| DocumentPrintingEventArgs | Class | The event arguments used by the DocumentPrinting event. |
| DocumentReloadLatestProgressChangedEventArgs | Class | The event arguments used during the reload latest phase of . |
| DocumentReloadedLatestEventArgs | Class | The event arguments used by the DocumentReloadedLatestEvent event. This event... |
| DocumentReloadingLatestEventArgs | Class | The event arguments used by the DocumentReloadingLatest event. |
| DocumentSaveToCentralProgressChangedEventArgs | Class | The event arguments used during the save to central phase of . |
| DocumentSaveToLocalProgressChangedEventArgs | Class | The event arguments used during the save to local phase of . |
| DocumentSavedAsEventArgs | Class | The event arguments used by the DocumentSavedAs event. |
| DocumentSavedEventArgs | Class | The event arguments used by the DocumentSaved event. |
| ... | | *39 more types, use search_api.py to query* |

## Autodesk.Revit.DB.ExtensibleStorage

**Type count**: 9

| Type | Kind | Description |
|------|------|------|
| AccessLevel | Enumeration | Defines access levels to objects in the Extensible Storage framework. |
| ContainerType | Enumeration | An enumerated type indicating if the field represents a single value or a con... |
| DataStorage | Class | An element which allows an API applications to organize and store data. |
| Entity | Class | An object stored in the Extensible Storage framework. An Entity is described ... |
| ExtensibleStorageFilter | Class | A filter used to filter elements with extensible storage data based on specif... |
| Field | Class | The description of a field within a Schema in the Extensible Storage framewor... |
| FieldBuilder | Class | This class is used to create Fields in the Extensible Storage framework. |
| Schema | Class | The description of a single object (Entity) in the Extensible Storage framewo... |
| SchemaBuilder | Class | This class is used to create Schemas in the Extensible Storage framework. |

## Autodesk.Revit.DB.ExternalService

**Type count**: 17

| Type | Kind | Description |
|------|------|------|
| DisparityResponse | Enumeration | An enumerated value to return from OnServerDiparity indicating what the servi... |
| ExecutionPolicy | Enumeration | Controls how servers of multi-server external services are executed. |
| ExternalService | Class | This base class represents an external service inside Revit application. |
| ExternalServiceId | Class | Unique identifier of an external service. |
| ExternalServiceOptions | Class | Various options affecting the behavior of an External Service |
| ExternalServiceRegistry | Class | This class gives access to external services. Use it to register external ser... |
| ExternalServiceResult | Enumeration | An enumerated value representing a result from executing an external service. |
| ExternalServices | Class | Provides a container of all Revit built-in ExternalServiceId instances. |
| ExternalServices.BuiltInExternalServices | Class | A collection of ids for external services that are managed and used by Revit ... |
| IExternalData | Interface | The base interface for data classes used when executing servers of external s... |
| IExternalServer | Interface | The base interface for all external servers. |
| IExternalService | Interface | The base interface class for all external services. |
| IMultiServerService | Interface | The base interface class for all multi-server services. |
| ISingleServerService | Interface | The base interface class for all single-server services. |
| MultiServerService | Class | This class represents a multi-server service inside Revit application. It is ... |
| ServerChangeCause | Enumeration | Indicates the cause for the active server to be changed |
| SingleServerService | Class | This class represents a single-server service inside Revit application. It is... |

## Autodesk.Revit.DB.Fabrication

**Type count**: 18

| Type | Kind | Description |
|------|------|------|
| DesignToFabricationConverter | Class | This class represents the MEP design to fabrication part convert tool. |
| DesignToFabricationConverterResult | Enumeration | Possible results from invoking the DesignToFabricationConverter. |
| DesignToFabricationMappingResult | Enumeration | Possible results from setting the mapping from Family symbols to Fabrication ... |
| FabricationAncillaryType | Enumeration | An enumerated type listing all fabrication ancillary types. |
| FabricationAncillaryUsageType | Enumeration | An enumerated type describing where an ancillary is used on a fabrication part. |
| FabricationCustomDataType | Enumeration | An enumerated type listing all fabrication custom data value types. |
| FabricationNetworkChangeService | Class | This class represents the fabrication part change service and change size tools. |
| FabricationNetworkChangeServiceResult | Enumeration | Possible results from invoking the FabricationNetworkChangeService. |
| FabricationPartCompareType | Enumeration | Fabrication Part Comparison Types |
| FabricationPartFitResult | Enumeration | Fabrication part stretch/fill result. |
| FabricationPartJustification | Enumeration | Fabrication part eccentric justifications for alignment for flat edged parts. |
| FabricationPartPlacementUtils | Class | General utility placement methods in the Autodesk Revit MEP product for fabri... |
| FabricationPartRouteEnd | Class | Class to hold fabrication part routing start or end information. |
| FabricationPartSizeMap | Class | This class represents the fabrication part size map for straights allowing th... |
| FabricationSaveJobOptions | Class | Options for FabricationPart.SaveAsFabricationJob() method. |
| FabricationUtils | Class | General utility methods in the Autodesk Revit MEP product for fabrication. |
| PartialFailureResults | Enumeration | Possible results of the partial failure from invoking the DesignToFabrication... |
| ValidationStatus | Enumeration | Lists the validation type of the fabrication part. |

## Autodesk.Revit.DB.IFC

**Type count**: 46

| Type | Kind | Description |
|------|------|------|
| ExporterIFC | Class | The main class provided by Revit to allow implementation of IFC export. |
| ExporterIFCUtils | Class | A class that contains utilities needed to implement Revit's version of the IF... |
| HostObjectSubcomponentInfo | Class | A class that contains roof or floor slab information, calculated by ExporterI... |
| IExporterIFC | Interface | The interface used to implement a custom IFC exporter. |
| IFCAggregate | Class | A collection of IFC handles or attributes. |
| IFCAggregateIterator | Class | A class used to iterate individual objects in an IFCAggregate. |
| IFCAnyHandle | Class | A handle representing an item in an IFC file. |
| IFCConnectedWallData | Class | A class that contains the IFC-specific information about how an element is jo... |
| IFCConnectedWallDataLocation | Enumeration | An enumerated type that represents the location where an element is connected... |
| IFCData | Class | A specialized type of abstract data block that can represent any data type. |
| IFCDataPrimitiveType | Enumeration | Used in operations to specify the primitive type of an IFCData. |
| IFCExtrusionAxes | Enumeration | Represents the possible axes to try when generating an extrusion using IFCCre... |
| IFCExtrusionBasis | Enumeration | This enumerated type represents the possible bases for derivation of extrusio... |
| IFCExtrusionCalculatorOptions | Class | This class contains the options used to calculate extrusions from Revit geome... |
| IFCExtrusionCalculatorUtils | Class | A utility class used to calculate extrusion data from Revit geometry for IFC ... |
| IFCExtrusionCreationData | Class | A utility object that is used to pass information related to extrusion creation. |
| IFCExtrusionData | Class | Represents the geometry of an extrusion (a solid body or opening) generated f... |
| IFCFamilyInstanceExtrusionExportResults | Class | This class represents the results of a geometric analysis of a family instance. |
| IFCFile | Class | Represents the IFC file which is being created during export. |
| IFCFileFormat | Enumeration | The IFC file format. |
| ... | | *26 more types, use search_api.py to query* |

## Autodesk.Revit.DB.Infrastructure

**Type count**: 6

| Type | Kind | Description |
|------|------|------|
| Alignment | Class | Represents an object which provides access to an underlying Revit alignment e... |
| AlignmentStationLabel | Class | Represents an object which provides access to a specialized Revit annotation ... |
| AlignmentStationLabelOptions | Class | Represents an object containing options for creating an . |
| AlignmentStationLabelSetOptions | Class | Represents an object containing options for creating a set of . |
| HorizontalCurveEndpoint | Class | Represents an endpoint (start or end) of a horizontal curve in geometry. |
| HorizontalCurveType | Enumeration | Represents the curve types for the horizontal curves which define geometry. |

## Autodesk.Revit.DB.Lighting

**Type count**: 29

| Type | Kind | Description |
|------|------|------|
| AdvancedLossFactor | Class | This class encapsulates advanced lighting loss factor calculation. |
| BasicLossFactor | Class | This class encapsulates basic lighting loss factor calculation. |
| CircleLightShape | Class | This class encapsulates a circle light shape. |
| ColorPreset | Enumeration | Preset values of initial colors for specific lighting types |
| CustomInitialColor | Class | This class encapsulates a custom initial lighting color. |
| HemisphericalLightDistribution | Class | This class encapsulates a hemispherical light distribution. |
| InitialColor | Class | This class is the base class for calculating initial light color. |
| InitialFluxIntensity | Class | This class encapsulates initial flux intensity calculation. |
| InitialIlluminanceIntensity | Class | This class encapsulates initial illuminance intensity calculation. |
| InitialIntensity | Class | This class is the base class for calculating lighting initial intensity. |
| InitialLuminousIntensity | Class | This class encapsulates initial luminous intensity calculation. |
| InitialWattageIntensity | Class | This class encapsulates initial wattage intensity calculation. |
| LightDimmingColor | Enumeration | Tags for specific light dimming colors |
| LightDistribution | Class | This class is the base class for specifying light distribution. |
| LightDistributionStyle | Enumeration | Tags for specific light distribution styles |
| LightFamily | Class | This class encapsulates light family information. |
| LightGroup | Class | This class represents a set of lights grouped together for easier management ... |
| LightGroupManager | Class | This class represents a set of light groups that are used for easier manageme... |
| LightShape | Class | This class is the base class for specifying light shape. |
| LightShapeStyle | Enumeration | Tags for specific light shape styles |
| ... | | *9 more types, use search_api.py to query* |

## Autodesk.Revit.DB.Macros

**Type count**: 15

| Type | Kind | Description |
|------|------|------|
| AddInIdAttribute | Class | The custom AddInId attribute for Macros macros use only. |
| ApplicationEntryPoint | Class | For Revit Macros use only. |
| ApplicationMacroOptions | Enumeration | The application macro options. |
| IEntryPoint | Interface | The interface supporting Document and Application level entry point classes f... |
| IModuleMaker | Interface | The interface used to create module project. |
| Macro | Class | An individual Autodesk Revit Macro which can be executed. |
| MacroEnvironment | Enumeration | The Macro environments. |
| MacroLanguageType | Enumeration | The Macro language types. |
| MacroManager | Class | Manager object for the Macro modules of the application. |
| MacroManagerIterator | Class | An iterator to a MacroManager. |
| MacroModule | Class | A container for individual macros. As it relates to the macros editor, one Ma... |
| MacroModuleIterator | Class | An iterator to the Macros in a MacroModule. |
| ModuleSettings | Class | The module settings. |
| ModuleStatus | Enumeration | The Macro module status. |
| VendorIdAttribute | Class | The custom VendorId attribute for Macros macros use only. |

## Autodesk.Revit.DB.Mechanical

**Type count**: 70

| Type | Kind | Description |
|------|------|------|
| AirCoolingCoilType | Enumeration | The type of air cooling coil. |
| AirFanType | Enumeration | The type of air fan. |
| AirHeatExchangerType | Enumeration | The type of air loop. |
| AirHeatingCoilType | Enumeration | The type of air heating coil. |
| AirSystemData | Class | Represents the data and parameter of analytical air system. |
| AnalyticalSystemDomain | Enumeration | The domain type of analytical system. |
| ComponentClassification | Enumeration | An enumerated type lists all MEP component classification. This attribute des... |
| ConditionType | Enumeration | An enumerated type listing all the possible condition types for a space object. |
| Duct | Class | A duct in the Autodesk Revit MEP product. |
| DuctFittingAndAccessoryConnectorData | Class | The input data used by external servers for calculation of the duct fitting a... |
| DuctFittingAndAccessoryData | Class | The input data used by external servers for calculation of the duct fitting a... |
| DuctFittingAndAccessoryPressureDropData | Class | The input and output data used by external servers for calculation of the duc... |
| DuctFittingAndAccessoryPressureDropItem | Class | A flow path of the duct/pipe fitting and accessory. It is defined by the begi... |
| DuctFlowConfigurationType | Enumeration | An enumerated type listing all duct flow configuration types for a connector. |
| DuctInsulation | Class | Represents insulation applied to the outside of a given duct , fitting or acc... |
| DuctInsulationType | Class | This class represents a duct insulation type in Autodesk Revit. |
| DuctLining | Class | Represents Lining applied to the inside of a given duct, fitting or accessory. |
| DuctLiningType | Class | This class represents a duct lining type in Autodesk Revit. |
| DuctLossMethodType | Enumeration | An enumerated type listing all duct loss calculation methods for a connector. |
| DuctPressureDropData | Class | The input and output data used by external servers for calculation of the duc... |
| ... | | *50 more types, use search_api.py to query* |

## Autodesk.Revit.DB.Plumbing

**Type count**: 29

| Type | Kind | Description |
|------|------|------|
| FlexPipe | Class | A flex pipe in the Autodesk Revit MEP product. |
| FlexPipeType | Class | A flex pipe type in the Autodesk Revit MEP product. |
| FlowConversionMode | Enumeration | Enumerated type listing possible flow conversion modes for piping calculations. |
| FluidTemperature | Class | Represents the dynamic viscosity and density properties as defined at a certa... |
| FluidTemperatureSetIterator | Class | An iterator to a set of FluidTemperature from FluidType. |
| FluidType | Class | Has been extended to provide read and write access to a collection of FluidTe... |
| IPipeFittingAndAccessoryPressureDropServer | Interface | Interface class for external servers implementing pipe fitting and pipe acces... |
| IPipePlumbingFixtureFlowServer | Interface | Interface class for external servers implementing Pipe plumbing fixture flow ... |
| IPipePressureDropServer | Interface | Interface for external servers implementing pipe pressure drop calculation. |
| Pipe | Class | A pipe in the Autodesk Revit MEP product. |
| PipeFittingAndAccessoryConnectorData | Class | The input data used by external servers for calculation of the pipe fitting a... |
| PipeFittingAndAccessoryData | Class | The input data used by external servers for calculation of the pipe fitting a... |
| PipeFittingAndAccessoryPressureDropData | Class | The input and output data used by external servers for calculation of the pip... |
| PipeFittingAndAccessoryPressureDropItem | Class | A flow path of the pipe/pipe fitting and accessory. |
| PipeFlowConfigurationType | Enumeration | An enumerated type listing all connector flow configuration |
| PipeFlowState | Enumeration | An enumerated type listing all the pipe flow states for a pipe |
| PipeInsulation | Class | Represents insulation applied to the outside of a given pipe, fitting or cont... |
| PipeInsulationType | Class | This class represents a pipe insulation type in Autodesk Revit. |
| PipeLossMethodType | Enumeration | An enumerated type listing all pipe loss method types for a connector |
| PipePlumbingFixtureFlowData | Class | The input and output data used by external servers for calculation of the pip... |
| ... | | *9 more types, use search_api.py to query* |

## Autodesk.Revit.DB.PointClouds

**Type count**: 14

| Type | Kind | Description |
|------|------|------|
| CloudPoint | Structure | Represents a point obtained from a Point cloud. |
| IPointCloudAccess | Interface | An interface that provides functionality for working with an individual Point... |
| IPointCloudEngine | Interface | An interface that controls the behavior of the link from Revit to a custom Po... |
| IPointSetIterator | Interface | An interface that Revit will call when iterating through sets of points on th... |
| PointCloudColorEncoding | Enumeration | The color encodings supported by Revit point clouds. |
| PointCloudColorSettings | Class | The color settings which are applied to a PointCloudInstance element, or one ... |
| PointCloudEngineRegistry | Class | This class supports registration of custom Point Cloud Engines in a Revit ses... |
| PointCloudFilter | Class | A class used to describe the criteria an application desires when obtaining m... |
| PointCloudFilterFactory | Class | A factory class for creating point cloud filters. |
| PointCloudFilterUtils | Class | Utilities specific to point cloud filters. |
| PointCloudOverrideSettings | Class | The graphic override settings for one PointCloudInstance element or one of it... |
| PointCloudOverrides | Class | Graphic overrides that are stored by a view to be applied to a PointCloudInst... |
| PointCollection | Class | A class that represents a set of points created and returned by Revit in resp... |
| PointIterator | Class | A class used to iterate individual points in a PointCollection. |

## Autodesk.Revit.DB.Steel

**Type count**: 1

| Type | Kind | Description |
|------|------|------|
| SteelElementProperties | Class | This class is used to attach steel fabrication information to various Revit e... |

## Autodesk.Revit.DB.Structure

**Type count**: 230

| Type | Kind | Description |
|------|------|------|
| AlignedDistributionRebarHandles | Enumeration | This enum represents the values that custom handles tags of RebarConstrainedH... |
| AlignedFreeFormSetOrientationOptions | Enumeration | Orientation options for Aligned Free Form Rebar set. |
| AnalyticalCurveSelector | Enumeration | Specifies which portion of an Analytical Curve is of interest. |
| AnalyticalElement | Class | Base class for a structural analytical elements. AnalyticalElement represents... |
| AnalyticalElementSelector | Enumeration | Specifies a portion of an Analytical Element or the whole element. |
| AnalyticalFixityState | Enumeration | Specifies the fixity setting of individual degrees of freedom in analytical r... |
| AnalyticalLink | Class | An analytical link element that is used to create connections between other A... |
| AnalyticalLinkType | Class | An object that specifies the analysis properties for an AnalyticalLink element. |
| AnalyticalLoopType | Enumeration | Specifies kind of analytical model loop. |
| AnalyticalMember | Class | Represents a linear element in the structural analytical model. |
| AnalyticalModelSelector | Class | Defines a portion of an Analytical Model for an Element. |
| AnalyticalNodeConnectionStatus | Enumeration | Indicates the Connections Status for an Analytical Node. |
| AnalyticalNodeData | Class | This class holds information related to analytical model. |
| AnalyticalOpening | Class | An element that represents an Opening in an Analytical Panel element. |
| AnalyticalPanel | Class | An element that represents a surface in the Structural Analytical Model. |
| AnalyticalRigidLinksOption | Enumeration | Specifies how Rigid Links will be made for the Analytical Model. |
| AnalyticalStructuralRole | Enumeration | Indicates the structural role for the analytical elements. |
| AnalyticalSupportPriority | Enumeration | Defines how "highly" another Element is giving support for one Element. |
| AnalyticalSupportType | Enumeration | Indicates what kind of support another Element provides -- Point, Surface, or... |
| AnalyticalSurfaceBase | Class | This is the base class for analytical surface elements. |
| ... | | *210 more types, use search_api.py to query* |

## Autodesk.Revit.DB.Structure.StructuralSections

**Type count**: 55

| Type | Kind | Description |
|------|------|------|
| StructuralElementDefinitionData | Class | Class containing information about section and position of the structural ele... |
| StructuralSection | Class | The base class for StructuralSection specific classes, designed to provide co... |
| StructuralSectionAnalysisParams | Class | Defines common set of parameters for structural analysis. |
| StructuralSectionCParallelFlange | Class | Defines parameters for C-channel Parallel Flange structural section. |
| StructuralSectionCProfile | Class | Defines parameters for C Profile structural section. |
| StructuralSectionCProfileWithFold | Class | Defines parameters for C Profile with fold structural section. |
| StructuralSectionCProfileWithLips | Class | Defines parameters for C Profile with lips structural section. |
| StructuralSectionCSlopedFlange | Class | Defines parameters for C-channel Sloped Flange structural section. |
| StructuralSectionColdFormed | Class | Defines parameters for Hot Formed structural section. |
| StructuralSectionConcreteCross | Class | Defines parameters for parameterized concrete cross structural section. |
| StructuralSectionConcreteRectangle | Class | Defines parameters for parameterized concrete rectangle structural section. |
| StructuralSectionConcreteRectangleCut | Class | Defines parameters for parameterized concrete rectangle cut structural section. |
| StructuralSectionConcreteRound | Class | Creates a new instance of Structural Section Concrete Round shape with the as... |
| StructuralSectionConcreteT | Class | Defines parameters for parameterized concrete T structural section. |
| StructuralSectionErrorCode | Enumeration | Error codes for StructuralSection related operations. |
| StructuralSectionGeneralC | Class | Defines parameters for Channel Cold Formed shape. |
| StructuralSectionGeneralCEx | Class | Defines parameters for Channel With Fold Cold Formed shape. |
| StructuralSectionGeneralF | Class | Defines parameters for Flat Bar. |
| StructuralSectionGeneralH | Class | Defines parameters for Rectangular Pipe structural section. |
| StructuralSectionGeneralI | Class | Defines parameters for general Double T shape. |
| ... | | *35 more types, use search_api.py to query* |

## Autodesk.Revit.DB.Visual

**Type count**: 103

| Type | Kind | Description |
|------|------|------|
| AdvancedGlazing | Class | A static class that provides access to the property names that appear in the ... |
| AdvancedLayered | Class | A static class that provides access to the property names that appear in the ... |
| AdvancedMetal | Class | A static class that provides access to the property names that appear in the ... |
| AdvancedOpaque | Class | A static class that provides access to the property names that appear in the ... |
| AdvancedTransparent | Class | A static class that provides access to the property names that appear in the ... |
| AdvancedWood | Class | A static class that provides access to the property names that appear in the ... |
| AppearanceAssetEditScope | Class | A scope object that provides special access and limitations related to editin... |
| Asset | Class | Represents a connected property of material. |
| AssetProperties | Class | Represents a set of asset property(s). |
| AssetProperty | Class | Represents a property of material. |
| AssetPropertyBoolean | Class | Represents a property of material. |
| AssetPropertyDistance | Class | Represents a property of material. |
| AssetPropertyDouble | Class | Represents a property of material. |
| AssetPropertyDoubleArray2d | Class | Represents a property consisting of an array of double values. |
| AssetPropertyDoubleArray3d | Class | Represents a vector or point property. |
| AssetPropertyDoubleArray4d | Class | Represents a color property of material. |
| AssetPropertyDoubleMatrix44 | Class | Represents a property consisting of an array of double values. |
| AssetPropertyEnum | Class | Represents a property of material. |
| AssetPropertyFloat | Class | Represents a property of material. |
| AssetPropertyFloatArray | Class | Represents a property consisting of an array of float values. |
| ... | | *83 more types, use search_api.py to query* |

## Autodesk.Revit.Exceptions

**Type count**: 62

| Type | Kind | Description |
|------|------|------|
| AccessDeniedException | Class |  |
| ApplicationException | Class | The exception that is thrown when a non-fatal application error occurs. |
| ArgumentException | Class | The exception that is thrown when one of the arguments provided to a method i... |
| ArgumentNullException | Class | The exception that is thrown when is passed to a method that does not accept ... |
| ArgumentOutOfRangeException | Class | The exception that is thrown when the value of an argument is outside the all... |
| ArgumentsInconsistentException | Class | The exception that is thrown when each individual argument is OK, but a joint... |
| AutoJoinFailedException | Class | The exception that is thrown when an autojoin operation failed. |
| BackgroundTaskCancelledException | Class | The exception thrown when Revit cancels a background operation. Third-party d... |
| CannotOpenBothCentralAndLocalException | Class | The exception thrown when both a central model and also a local file for the ... |
| CentralFileCommunicationException | Class | The exception thrown when there is a network communication error involving a ... |
| CentralModelAccessDeniedException | Class | The exceptions thrown when a central model can be reached but access is denie... |
| CentralModelAlreadyExistsException | Class | Exception is thrown when the central model already exists at the specified lo... |
| CentralModelContentionException | Class | The exception thrown when a central model is busy (locked) and the operation ... |
| CentralModelException | Class | The base class for exceptions that are common to both file-based and server-b... |
| CentralModelVersionArchivedException | Class | Exception is thrown when last central version merged into the local model has... |
| CheckoutElementsRequestTooLargeException | Class | Exception is thrown when too many elements are requested for checkout |
| CorruptModelException | Class | The exception that is thrown when the model is or seems corrupt. |
| DefaultValueException | Class | The exception thrown when Revit cannot initialize a default value for a famil... |
| DirectoryNotEmptyException | Class | The exception that is thrown when a method received a directory as an argumen... |
| DirectoryNotFoundException | Class | The exception that is thrown when the specified directory could not be found. |
| ... | | *42 more types, use search_api.py to query* |

## Autodesk.Revit.UI

**Type count**: 104

| Type | Kind | Description |
|------|------|------|
| AddInCommandBinding | Class | This object represents a binding between a Revit command and one or more hand... |
| ButtonData | Class | Base class used to contain information necessary to construct a button in the... |
| ColorSelectionDialog | Class | Allows display of the Revit Color dialog. |
| ComboBox | Class | This class represents a selection control with a drop-down list that can be s... |
| ComboBoxData | Class | This class contains information necessary to construct a combo box in the Rib... |
| ComboBoxMember | Class | This class represents an item in the drop-down list of a ComboBox. |
| ComboBoxMemberData | Class | This class contains information necessary to construct a ComboBoxMember. |
| CommandMenuItem | Class | A class representing a single command menu item. |
| ContextMenu | Class | A class that owns context menu items. |
| ContextualHelp | Class | Contains the details for how Revit should allow invocation of contextual help... |
| ContextualHelpType | Enumeration | Represents the contextual help type. |
| DockPosition | Enumeration | Which part of the Revit application frame the pane should dock to. |
| DockablePane | Class | A user interface pane that participates in Revit's docking window system. |
| DockablePaneId | Class | Identifier for a pane that participates in the Revit docking window system. |
| DockablePaneProviderData | Class | Information about a new dockable pane being added to the Revit user interface. |
| DockablePaneState | Class | Describes where a dockable pane window should appear in the Revit user interf... |
| DockablePanes | Class | Provides a container of all Revit built-in DockablePaneId instances. |
| DockablePanes.BuiltInDockablePanes | Class | A collection of ids of the dockable panes provided by Revit. |
| DoubleClickAction | Enumeration | Possible actions Revit can take in response to the user double-clicking on an... |
| DoubleClickOptions | Class | Provides access to settings that control what happens when the current user d... |
| ... | | *84 more types, use search_api.py to query* |

## Autodesk.Revit.UI.Events

**Type count**: 30

| Type | Kind | Description |
|------|------|------|
| ApplicationClosingEventArgs | Class | The event arguments used by the ApplicationClosing event. |
| BeforeExecutedEventArgs | Class | The event arguments used by AddInCommandBinding's BeforeExecuted event. |
| CanExecuteEventArgs | Class | The event arguments used by AddInCommandBinding's CanExecute event. |
| ComboBoxCurrentChangedEventArgs | Class | The event arguments used by ComboBox's CurrentChanged event. |
| ComboBoxDropDownClosedEventArgs | Class | The event arguments used by ComboBox's DropDownClosed event. |
| ComboBoxDropDownOpenedEventArgs | Class | The event arguments used by ComboBox's DropDownOpened event. |
| CommandEventArgs | Class | The base class of the command Executed and CanExecute event arguments. |
| DialogBoxData | Class | An object that is passed to your application when a dialog is displayed in Re... |
| DialogBoxShowingEventArgs | Class | The base class for the event arguments used by the DialogBoxShowing event. |
| DisplayingOptionsDialogEventArgs | Class | The event arguments used by DisplayingOptionDialog event. |
| DockableFrameFocusChangedEventArgs | Class | The event arguments used by the DockableFrameActivatedChanged event. |
| DockableFrameVisibilityChangedEventArgs | Class | The event arguments used by the DockableFrameVisibilityChanged event. |
| ExecutedEventArgs | Class | The event arguments used by AddInCommandBinding's Executed event. |
| ExternalDataManagerChangedEventArgs | Class | The event arguments used by the ExternalDataManager changed event. |
| FabricationPartBrowserChangedEventArgs | Class | The event arguments used by the FabricationPartBrowserChangedEventArgs event. |
| FabricationPartBrowserOperation | Enumeration | Operations for the FabricationPartBrowserChangedEventArgs Event |
| FormulaEditingEventArgs | Class | The event arguments used by the DocumentSaving event. |
| IdlingEventArgs | Class | The event arguments used by the Idling event. |
| MacroUpdatedEventArgs | Class | The event arguments used by the MacroUpdated event. |
| MessageBoxData | Class | An object that represents a simple message box that prompts the user for some... |
| ... | | *10 more types, use search_api.py to query* |

## Autodesk.Revit.UI.Macros

**Type count**: 2

| Type | Kind | Description |
|------|------|------|
| ApplicationEntryPoint | Class | For Revit Macros use only. |
| UIMacroManager | Class | The UI macro manager to support Macro operations. |

## Autodesk.Revit.UI.Mechanical

**Type count**: 3

| Type | Kind | Description |
|------|------|------|
| DuctFittingAndAccessoryPressureDropUIData | Class | The input and output data used by external UI servers for storing UI settings. |
| DuctFittingAndAccessoryPressureDropUIDataItem | Class | Each duct fitting or duct accessory FamilyInstance has one DuctFittingAndAcce... |
| IDuctFittingAndAccessoryPressureDropUIServer | Interface | Interface for external servers providing optional UI for duct fitting and duc... |

## Autodesk.Revit.UI.Plumbing

**Type count**: 3

| Type | Kind | Description |
|------|------|------|
| IPipeFittingAndAccessoryPressureDropUIServer | Interface | Interface for external servers providing optional UI for pipe fitting and pip... |
| PipeFittingAndAccessoryPressureDropUIData | Class | The input and output data used by external UI servers for storing UI settings. |
| PipeFittingAndAccessoryPressureDropUIDataItem | Class | The input and output data used by external UI servers for initializing and st... |

## Autodesk.Revit.UI.Selection

**Type count**: 7

| Type | Kind | Description |
|------|------|------|
| ISelectionFilter | Interface | An interface that provides the ability to filter objects during a selection o... |
| ObjectSnapTypes | Enumeration | This enumerated type contains object snap types allowed to be set during Pick... |
| ObjectType | Enumeration | This enumerated type contains object types allowed to be selected during sele... |
| PickBoxStyle | Enumeration | The enum that controls the style of the pick box. |
| PickedBox | Class | A class that contains two XYZ points representing the pick box on the screen. |
| SelectableInViewFilter | Class | A filter that passes elements that are selectable in the given view. |
| Selection | Class | Contains the current user selection of elements within the project. |
