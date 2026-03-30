using Autodesk.Revit.DB;
using SmartCon.Core.Math;

namespace SmartCon.Core.Models;

/// <summary>
/// Иммутабельный снапшот состояния коннектора на момент выбора.
/// Не хранить между транзакциями — пересоздавать из актуального Connector (I-05).
/// Все размеры в Internal Units (decimal feet, I-02).
/// </summary>
public sealed record ConnectorProxy
{
    public required ElementId OwnerElementId { get; init; }
    public required int ConnectorIndex { get; init; }
    public required XYZ Origin { get; init; }
    public required XYZ BasisZ { get; init; }
    public required XYZ BasisX { get; init; }
    public required double Radius { get; init; }
    public required Domain Domain { get; init; }
    public required ConnectionTypeCode ConnectionTypeCode { get; init; }
    public required bool IsFree { get; init; }

    /// <summary>
    /// Origin как Vec3 для алгоритмов Core (ConnectorAligner, VectorUtils).
    /// Конвертация без Revit runtime: доступ к .X/.Y/.Z — compile-time (I-09).
    /// </summary>
    public Vec3 OriginVec3 => new(Origin.X, Origin.Y, Origin.Z);

    /// <summary>BasisZ как Vec3.</summary>
    public Vec3 BasisZVec3 => new(BasisZ.X, BasisZ.Y, BasisZ.Z);

    /// <summary>BasisX как Vec3.</summary>
    public Vec3 BasisXVec3 => new(BasisX.X, BasisX.Y, BasisX.Z);
}
