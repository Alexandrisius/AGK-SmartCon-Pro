if True: #

	import clr

	clr.AddReference('ProtoGeometry')
	import Autodesk.DesignScript.Geometry as DG

	clr.AddReference('DSCoreNodes')
	import DSCore as DS

	clr.AddReference("RevitServices")
	from RevitServices.Persistence import DocumentManager
	from RevitServices.Transactions import TransactionManager
	doc =  DocumentManager.Instance.CurrentDBDocument

	clr.AddReference("RevitAPI")
	from Autodesk.Revit.DB import *
	from Autodesk.Revit.DB.Plumbing import *
	from Autodesk.Revit.DB.Mechanical import *
	from Autodesk.Revit.DB.Structure import *

	clr.AddReference("RevitNodes")
	import Revit.Elements as DR
	import Revit
	clr.ImportExtensions(Revit.Elements)
	clr.ImportExtensions(Revit.GeometryConversion)

	import math
	import time


	start_time = time.time()
	from System.Collections.Generic import List as CList
	from System.Collections.Generic import IList

def ClosestConnectors(mepCurve1Connectors, mepCurve2Connectors):

	minDist = 9999999
	closestConnectors = None
	for connector1 in mepCurve1Connectors:
		for connector2 in mepCurve2Connectors:
			dist =  connector1.Origin.DistanceTo(connector2.Origin)
			if dist < minDist:
				minDist = dist
				closestConnectors = [connector1, connector2]
	return closestConnectors




def RebuildLoop(curves):
	
	
	def Compare(curve1,curve2,maxAngle):	
	
		v1 = curve1.ComputeDerivatives(1,True).BasisX
		v2= curve2.ComputeDerivatives(0,True).BasisX
		p11 = curve1.Evaluate(0, True)
		pmid = curve1.Evaluate(0.5, True)
		p12 = curve1.Evaluate(1, True)
		p21 = curve2.Evaluate(0, True)
		p22 = curve2.Evaluate(1, True)
		type1 = curve1.GetType()
		type2 = curve2.GetType()
		angle = math.degrees(v1.AngleOnPlaneTo(v2,XYZ(0,0,1)))
		dist = p12.DistanceTo(p21) 
		if type1 == type2 and  (angle <= maxAngle or angle >= 360 - maxAngle)  and dist < 0.1:
			if type == Arc:
				return Arc.Create(p11,p22,pmid)	
			else: return Line.CreateBound(p11,p22)	
		else:
			return None
	i=0
	
	while i< len(curves)-1:
		com = Compare(curves[i],curves[i+1],1)
		if com is None:
			i+=1
		else:
			del curves[i+1]
			curves[i] = com				
			
				
	#com = Compare(curves[-1],curves[0],10)			
	#if com is not None:
	#	del curves[-1]
	#	curves[0] = com
			
	return curves
			



def SortCurves(curves):

	curves = [c.ToProtoType() for c in curves]
	polyCurve = DG.PolyCurve.ByJoinedCurves(curves,0.1)
	box = polyCurve.BoundingBox
	minp, maxp = box.MinPoint, box.MaxPoint
	p1 = DG.Point.ByCoordinates(minp.X,(maxp.Y + minp.Y)/2,minp.Z)
	p2 = polyCurve.ClosestPointTo(p1)
	parameter = polyCurve.ParameterAtPoint(p2)
	tangent = polyCurve.TangentAtParameter(parameter)
	if tangent.Y > 0: polyCurve = polyCurve.Reverse()
	return [c.ToRevitType() for c in polyCurve.Explode()]

def CreateOffsetCurves(curves,offset,baseZ,start):


	# Проверить чтобы не было витражей

	roofTypes = list(FilteredElementCollector(doc).OfClass(RoofType))
	roofType = [rt for rt in roofTypes if rt.GetCompoundStructure() is not None][0]


	level = list(FilteredElementCollector(doc).OfClass(Level).ToElements())[0]

	curveArray = CurveArray()
	modelCurveArray= clr.StrongBox[ModelCurveArray](ModelCurveArray())
	for c in curves:
		curveArray.Append(c)
	roof = doc.Create.NewFootPrintRoof(curveArray,level,roofType,modelCurveArray)

	for c in list(modelCurveArray.Value):
		c.get_Parameter(BuiltInParameter.ROOF_CURVE_IS_SLOPE_DEFINING).Set(1)
		c.get_Parameter(BuiltInParameter.ROOF_SLOPE).Set(1)

	doc.Regenerate()

	curveGroups = []
	try:

		opt = Options()
		solid = list(roof.get_Geometry(opt))[0]
		upFaces = [f for f in solid.Faces if f.ComputeNormal(UV(0,0)).Z > 0.05]
		minz = min([f.Evaluate(f.GetBoundingBox().Min).Z for f in upFaces ])
		maxz = max([f.Evaluate(f.GetBoundingBox().Max).Z for f in upFaces ])

		def SplitSolid(solid,zz,baseZ):

			plane = Plane.CreateByNormalAndOrigin(XYZ(0,0,-1),XYZ(0,0,zz))
			faces = BooleanOperationsUtils.CutWithHalfSpace(solid,plane).Faces

			for face in faces:
				if face.FaceNormal.AngleTo(XYZ(0,0,1)) < math.radians(3):
					loops = list(face.GetEdgesAsCurveLoops())
					for loop in loops:
						for face in upFaces:
							point = list(loop)[0].Evaluate(0.5,True)
							res = face.Project(point)
							if res is not None and res.Distance < 0.01:


								tr = Transform.CreateTranslation(XYZ(0, 0, - zz + baseZ))
								translatedLoop = [l.CreateTransformed(tr) for l in loop]
								return translatedLoop


		zz = minz + offset/2
		while zz <= maxz:
			res = SplitSolid(solid,zz,baseZ)
			curveGroups.append(res)
			zz += offset

	except: pass

	doc.Delete(roof.Id)
	return curveGroups

def Shot(point,vector,curves):

	ray = Line.CreateBound(point, point.Add(vector.Multiply(1000)))
	
	data = []
	
	for i,curve in enumerate(curves):
	
		startPoint = curve.Evaluate(0,True)
		v1 = startPoint.Subtract(point).Normalize()
		angle = vector.AngleTo(v1)
		
		if angle < math.radians(1):# or angle > math.radians(360-3):
		
			parameterOnRay = ray.Project(startPoint).Parameter
			parameterOnCurve = 0
			data.append([ i, startPoint, parameterOnCurve, parameterOnRay])
			
		else: 
			#continue
			resultList = clr.StrongBox[IList[ClosestPointsPairBetweenTwoCurves]](CList[ClosestPointsPairBetweenTwoCurves]())
			try: closestPoint = ray.ComputeClosestPoints(curve, True,True,True, resultList)
			except: continue	
			
			resultList = list(resultList.Value)
			if resultList:
				res = resultList[0]
				point1 = res.XYZPointOnFirstCurve
				point2 = res.XYZPointOnSecondCurve
				
				parameterOnRay = res.ParameterOnFirstCurve
				parameterOnCurve = res.ParameterOnSecondCurve
				
				if point1.DistanceTo(point2) < 0.001 and parameterOnCurve < curve.Length - 0.01:
					data.append([i, point2, parameterOnCurve, parameterOnRay])
			else: pass

			#return shiftCurves, point2, parameter
	#return data		
	if data:
	
		data.sort(key = lambda x: x[-1])
		dd = data[0]
		index = dd[0]
		shiftCurves =  curves[index:] + curves[:index]

		return [shiftCurves,dd[1],dd[2]]
	else:
		return None

def Champfer(line1,line2,x):

	angle = line1.Direction.AngleTo(line2.Direction.Negate())
	if angle < math.pi/2-0.05:
		y = math.fabs((x/2)/math.sin(angle/2))
		if y > 0 and y < line1.Length/2 and y < line2.Length/2:
			p1 = line1.Evaluate(0,True)
			p2 = line1.Evaluate(line1.Length-y,False)
			p3 = line2.Evaluate(y,False)
			p4 = line2.Evaluate(1,True)
			line3 = Line.CreateBound(p1,p2)
			line4 = Line.CreateBound(p2,p3)
			line5 = Line.CreateBound(p3,p4)
			return line3,line4,line5

	else: return None

def maincode():

# Входные данные

	onlyTest = IN[0]

	region = UnwrapElement(IN[1])

	#return boundaris

	startPipe = UnwrapElement(IN[3])
	endPipe = UnwrapElement(IN[2])
	offset = IN[4]*2/304.8

	champfer = IN[5]/304.8
	reverse = IN[6]
	#inputParameter = IN[7]




# Обработка входных данных

	if region.GetType() != FilledRegion: return "Выбранный объект не является областью заливки."
	curves = list(UnwrapElement(IN[1]).GetBoundaries())
	if len(curves) > 1: return "Наличие двух контуров у региона не допускается/ Уберите внутренний контур у выбранной области"
	else: curves = curves[0]


	start = offset
	if offset < 100/304.8: return "Слишком маленький шаг между трубами"
	if champfer < 100/304.8: return "Слишком маленькая фаска"
	if startPipe.GetType() != Pipe: return "Выбранный элемент внутреннего контура не является трубой. Выберите заново"
	if endPipe.GetType() != Pipe: return "Выбранный элемент внешнего контура не является трубой. Выберите заново"


	level = startPipe.ReferenceLevel
	offsetFromLevel = startPipe.LevelOffset
	baseZ = level.Elevation + offsetFromLevel


	pipeType = startPipe.PipeType
	pipeDiameter = startPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).AsDouble()
	startPipeSystemType = doc.GetElement(startPipe.MEPSystem.GetTypeId())
	endPipeSystemType = doc.GetElement(endPipe.MEPSystem.GetTypeId())
	systemTypes = [startPipeSystemType,endPipeSystemType]


# Сортируем, Разворачиваем и проецируем кривые на нужную плоскость

	try:
		curves = SortCurves(curves)
		cg = CreateOffsetCurves(curves, offset, baseZ, start)
	except:
		return "В выбранном контуре ошибки. Скорее всего он разомкнут, либо вы выбрали два контура"

# Ищем стартовую точку (потом этого не будет)

	i = 0

	vector = XYZ(0,0,1)

	dyncurves = [c.ToProtoType() for c in cg[0]]
	polyCurve = DG.PolyCurve.ByJoinedCurves(dyncurves,0.1)
	#startPoint = polyCurve.PointAtParameter(inputParameter).ToXyz()
	startPoint = polyCurve.ClosestPointTo(startPipe.Location.Curve.ToProtoType()).ToXyz()

	distance = polyCurve.DistanceTo(startPipe.Location.Curve.ToProtoType())
	if distance - offset > 5000: return distance,"Поднесите входную трубу ближе к контуру",polyCurve.ClosestPointTo(startPipe.Location.Curve.ToProtoType())

# Разворот

	if reverse:
		for i in range(len(cg)):
			cg[i].reverse()
			for j in range(len(cg[i])):
				cg[i][j] = Line.CreateBound(cg[i][j].Evaluate(1,True),cg[i][j].Evaluate(0,True))



# Основной алгортм

	##a = [1,2,3,4]
	#return a[:5]

	#return [[c.ToProtoType().ExtendEnd(100) for c in g] for g in cg]

	report = []
	points = []

	for i in range(len(cg)):

		result = Shot(startPoint, vector , cg[i] )
		#return result
		if result is not None and startPoint.DistanceTo(result[1]) <= 5*offset:
			cg[i], shotPoint, parameter = result[0], result[1], result[2]

		else:
			if i > 0:
				try:
					poly = polyCurve.ByJoinedCurves([c.ToProtoType() for c in cg[i]],0.1)
					closestPoint1 = poly.ClosestPointTo(startPoint.ToPoint()).ToXyz()
					
					minDist = 999999
					index = 0
					for j,curve in enumerate(cg[i-1]):
						res = curve.Project(closestPoint1)
						distance = round(res.Distance/10) # Округляем
						if distance <= minDist:
							minDist = distance
							closestPoint2 = res.XYZPoint
							index = j
				
										
					cg[i-1] = cg[i-1][:index + 1]
					cg[i-1][-1] = Line.CreateBound(cg[i-1][-1].Evaluate(0,True),closestPoint2)
					vector = closestPoint1.Subtract(closestPoint2).Normalize()
					result = Shot(closestPoint2, vector , cg[i] )
					
					if result is not None:
						
						cg[i], shotPoint, parameter = result[0], result[1], result[2]
						report.append([shotPoint.ToPoint(),closestPoint1.ToPoint(),closestPoint2.ToPoint()])
						#return report
				except: 
					break
					#report.pop()
					report.append([shotPoint.ToPoint(),closestPoint1.ToPoint(),closestPoint2.ToPoint()])#,cg[i-1][-1].ToProtoType()])
					#break#return report
					#return report
					
			else: break

		v1,v2,v3 = cg[i][-1].Direction, cg[i][0].Direction, cg[i][1].Direction

		k = -1 if reverse else 1

		angle1 = v1.AngleOnPlaneTo(v2,XYZ(0,0,1*k))
		angle2 = v2.AngleOnPlaneTo(v3,XYZ(0,0,1*k))

		if angle1 < math.pi and angle2!= 0:
			leftOffset = offset/math.sin(angle1)
		else:
			leftOffset = offset 

		if angle2 < math.pi and angle2!= 0:
			rightOffset =  offset/math.sin(angle2)
		else:
			rightOffset = offset


# Если точка в начале прямой
		vector = XYZ(0,0,1)
		met = None
		try:
			
			if parameter < 2*leftOffset:
			
				met = 1
				cg[i][0] = Line.CreateBound(shotPoint, cg[i][0].Evaluate(1,True))
				parameter2 = cg[i][-1].Length - leftOffset
				
				if parameter2 > 0:
					cg[i][-1] = Line.CreateBound(cg[i][-1].Evaluate(0,True), cg[i][-1].Evaluate(parameter2,False))
				else:
					cg[i].pop()	
						
				if angle1 < math.pi:		
					vector = cg[i][0].Direction.Multiply(0.5)
				else:
					vector = cg[i][0].Direction.Multiply(-0.5)
				
					#met = 2
					#cg[i][0] = Line.CreateBound(shotPoint, cg[i][0].Evaluate(1,True))
					#parameter2 = cg[i][-1].Length - leftOffset
					#if parameter2 > 0:
					
					#	cg[i][-1] = Line.CreateBound(cg[i][-1].Evaluate(0,True), cg[i][-1].Evaluate(parameter2,False))
					#else:
					#	cg[i].pop()							
					
	
	
# Если посередине
	
			elif parameter <= cg[i][0].Length - rightOffset :
				met = 3
				if angle1 < math.pi:
					k = -1
				else:
					k = 1
	
				vector = cg[i][0].Direction.CrossProduct(XYZ.BasisZ).Multiply(-0.5)
				cg[i][0] = Line.CreateBound(shotPoint, cg[i][0].Evaluate(1,True))
				
				# Может быть и так cg[i].append(Line.CreateBound(cg[i][-1].Evaluate(1,True), cg[i][0].Evaluate(0-leftOffset,False)))
				cg[i].append(Line.CreateBound(cg[i][-1].Evaluate(1,True), cg[i][0].Evaluate(-offset,False)))
# Если в конце
	
			else:
				if angle2 < math.pi:
					met = 4
					#parameter = cg[i][0].Length-rightOffset if rightOffset < cg[i][0].Length else 0
					cg[i][0] = Line.CreateBound(cg[i][0].Evaluate(0,False), cg[i][0].Evaluate(cg[i][0].Length-rightOffset,False))
					cg[i] = cg[i][1:]+cg[i][:1]
					vector = cg[i][0].Direction.Multiply(0.5)
	
				else:
					met = 5
					cg[i][1] = Line.CreateBound(cg[i][1].Evaluate(leftOffset,False), cg[i][1].Evaluate(1,True))
					cg[i] = cg[i][1:]+cg[i][:1]
					vector = cg[i][-1].Direction.Multiply(0.5)
					
		except: pass
		
		#if vector is not None:
		#if reverse:
		#vector = vector.Negate()
		p = cg[i][-1].Evaluate(1,True)
		demoLine = Line.CreateBound(p, p.Add(vector)).ToProtoType()
		startPoint = cg[i][-1].Evaluate(1,True)
		
		for c in cg[i]: 
			try:
				report.append(c.ToProtoType())
			except: report.append(None)
		report.append([demoLine,parameter,met ])
		#return report
		
		
	
	#return report
	
	for g in cg:
		for c in g:
			#report.append([c.ToProtoType()])
			points.append(c.Evaluate(0,True))
		points.append(g[-1].Evaluate(1,True))	
	#return report

	points = [p.ToPoint() for p in points]
	points = DG.Point.PruneDuplicates(points,10)
	points = [p.ToXyz() for p in points]

	lines = []
	for i in range(len(points)-1):
		lines.append(Line.CreateBound(points[i],points[i+1]).ToProtoType())


	

	#return lines,[p.ToPoint() for p in points]#,report


	poly = DG.PolyCurve.ByJoinedCurves(lines,0.1)
	lines = [c.ToRevitType() for c in poly.Explode()]

	lines = RebuildLoop(lines)

	loop = CurveLoop.Create(CList[Curve](lines))
	loop1 = list(loop)

	k = -1 if reverse else 1

	try: loop2 = list(CurveLoop.CreateViaOffset(loop,offset/2,XYZ(0,0,1*k)))
	except: return "Не получилось создать оффсет. Попробуйте незначительно передвинуть трубу внутреннего контура в сторону"

	p1 = loop1[-1].Evaluate(1,True)
	p2 = loop2[-1].Evaluate(1,True)
	p3 = p1.Add(p2).Multiply(0.5)

	line1 = Line.CreateBound(p1,p3)
	line2 = Line.CreateBound(p3,p2)

	loop2.reverse()
	loop2 = [Line.CreateBound(c.Evaluate(1,True),c.Evaluate(0,True)) for c in loop2]

	allLines = loop1 + [line1,line2] + loop2

	inLines = loop1 + [line1]
	outLines = [line2] + loop2


	#return [a.ToProtoType() for a in allLines]

# Добавление краев

	startPipeLine = startPipe.Location.Curve.ToProtoType()
	firstCurve = inLines[0].ToProtoType()
	p1 =startPipeLine.ClosestPointTo(firstCurve)
	p2 = firstCurve.ClosestPointTo(startPipeLine)
	line1 = DG.Line.ByStartPointEndPoint(p1,p2).ToRevitType()
	inLines.insert(0,line1)

	lastCurve = outLines[-1].ToProtoType()
	endPipeLine = endPipe.Location.Curve.ToProtoType()
	p1 = lastCurve.ClosestPointTo(endPipeLine)
	p2 = endPipeLine.ClosestPointTo(lastCurve)

	outLines[-1] = DG.Line.ByStartPointEndPoint( lastCurve.StartPoint,p1).ToRevitType()
	try:
		line1 = DG.Line.ByStartPointEndPoint(p1,p2).ToRevitType()
		outLines.append(line1)
	except: pass	

	lineGroups = [inLines,outLines]
	pipes = []
	connectors = []
	
	
	
	
	

	#return [a.ToProtoType() for a in outLines],[a.ToProtoType() for a in inLines]

# Создание труб

	for i,lines in enumerate(lineGroups):

		j = 0
		while j < len(lines) - 1:
			res = Champfer(lines[j],lines[j+1],champfer)
			if res is not None:
				lines[j] = res[0]
				lines[j+1] = res[2]
				lines.insert(j+1,res[1])
				j+=2
			else:
				j+=1

	#return [[a.ToProtoType() for a in lines] for lines in lineGroups],[a.Evaluate(0.3 ,False).ToPoint() for a in lineGroups[1]]# [a.ToProtoType() for a in outLines]

	lengths = []
	types = ["Внутренний контур ","Внешний контур "]
	allLen = 0
	for i,lines in enumerate(lineGroups):
		sumLen = 0
		for line in lines:
			sumLen += line.Length*304.8/1000
			p1 = line.Evaluate(0,True)
			p2 = line.Evaluate(1,True)

			pipe = Pipe.Create(doc,systemTypes[i].Id, pipeType.Id, level.Id, p1, p2)
			pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(pipeDiameter)

			connectors.append( list(pipe.ConnectorManager.Connectors))
			if onlyTest:
				pipe = pipe.ToDSType(False)
			pipes.append(pipe)
		lengths.append(types[i]+str(round(sumLen,2))+" м")
		allLen += sumLen
	lengths.append("Сумма "+str(round(allLen,2))+" м")	
		
	doc.Regenerate()

	elbows = []
	for i in range(len(connectors)-1):

		elbow  = None

		cc = ClosestConnectors(connectors[i],connectors[i+1])
		try:
			elbow = doc.Create.NewElbowFitting(cc[0],cc[1])

		except: continue

		if onlyTest:
			elbow = elbow.ToDSType(False)
		elbows.append(elbow)

	return lengths,pipes,elbows



TransactionManager.Instance.EnsureInTransaction(doc)
x = maincode()
TransactionManager.Instance.TransactionTaskDone()

OUT = x
