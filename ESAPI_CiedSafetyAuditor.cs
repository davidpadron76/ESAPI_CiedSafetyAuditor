using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]
[assembly: AssemblyInformationalVersion("1.0")]

namespace VMS.TPS
{
    // 1. CLASE PRINCIPAL (Punto de entrada nativo de Eclipse)
    public class Script
    {
        public Script()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context /*, System.Windows.Window window, ScriptEnvironment environment*/)
        {
            try
            {
                // Validación de seguridad del contexto del TPS
                if (context.ExternalPlanSetup == null || context.StructureSet == null)
                {
                    MessageBox.Show(
                      "Error crítico de contexto:\n\nPor favor, asegúrese de abrir un plan de tratamiento externo calculado antes de ejecutar este script.",
                      "ESAPI CiedSafetyAuditor",
                      MessageBoxButton.OK,
                      MessageBoxImage.Error
                    );
                    return;
                }

                // FASE 1: Analizador Estructural
                CiedStructureAnalyzer analyzer = new CiedStructureAnalyzer(context);
                Structure detectedCied = analyzer.FindCiedStructure();

                if (detectedCied != null)
                {
                    // FASE 2: Extracción Dosimétrica
                    CiedDoseExtractor doseExtractor = new CiedDoseExtractor(context.ExternalPlanSetup, detectedCied);
                    doseExtractor.ExtractDoseData();

                    // FASE 3: Auditoría de Haces y Geometría Periférica
                    CiedBeamAuditor beamAuditor = new CiedBeamAuditor(context.ExternalPlanSetup, detectedCied);
                    beamAuditor.AuditBeams();

                    // FASE 4: Motor de Evaluación de Riesgo (TG-203)
                    CiedRiskEvaluator riskEvaluator = new CiedRiskEvaluator(
                      doseExtractor.DmaxGy,
                      beamAuditor.HasHighEnergyRisk,
                      beamAuditor.MinDistanceToEdgeCm,
                      beamAuditor.HasGeometryData
                    );
                    riskEvaluator.EvaluateRisk();

                    // REPORTE CLÍNICO CONSOLIDADO FINAL: ventana visual con semáforo por ítem
                    // (en vez de texto plano) para que el umbral y el nivel de riesgo de cada
                    // medición sean explícitos, no solo el veredicto final.
                    CiedAuditReportWindow reportWindow = new CiedAuditReportWindow(
                      detectedCied, doseExtractor, beamAuditor, riskEvaluator
                    );
                    reportWindow.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                string mensajeError = string.Format("Ocurrió una excepción inesperada durante la auditoría:\n\n{0}", ex.Message);
                MessageBox.Show(mensajeError, "Fallo Crítico del Sistema", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // 2. CLASE AUXILIAR - FASE 1 (Analizador de Estructuras)
    public class CiedStructureAnalyzer
    {
        private readonly ScriptContext _context;

        public CiedStructureAnalyzer(ScriptContext context)
        {
            _context = context;
        }

        private static readonly Regex RegexCied = new Regex(
          @"(cied|marcapaso|pacemaker|icd|desfibrilador|generador)",
          RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        public Structure FindCiedStructure()
        {
            List<Structure> candidatas = new List<Structure>();

            foreach (Structure structure in _context.StructureSet.Structures)
            {
                if (structure.IsEmpty)
                {
                    continue;
                }

                if (RegexCied.IsMatch(structure.Id))
                {
                    candidatas.Add(structure);
                }
            }

            if (candidatas.Count == 0)
            {
                MessageBox.Show(
                  "ALERTA DE PROTECCIÓN RADIOLÓGICA:\n\n" +
                  "No se encontró ninguna estructura asociada a un dispositivo cardíaco (CIED) en el plan activo.\n\n" +
                  "Asegúrese de que el dispositivo esté contorneado de forma correcta.",
                  "Barrera de Seguridad - Estructura Ausente",
                  MessageBoxButton.OK,
                  MessageBoxImage.Warning
                );

                return null;
            }

            if (candidatas.Count == 1)
            {
                WarnIfImplausibleDicomType(candidatas[0]);
                return candidatas[0];
            }

            // El orden de StructureSet.Structures no está garantizado por ESAPI, así que con
            // varias coincidencias (p.ej. "CIED" y "CIED_PRV") no se puede tomar la primera sin
            // riesgo de resultados no deterministas. Se elige la de menor volumen (heurística: el
            // contorno del dispositivo suele ser más pequeño que un PRV o una expansión) y se
            // informa explícitamente al usuario para que verifique la selección.
            Structure seleccionada = candidatas.OrderBy(s => s.Volume).First();
            string listaCandidatas = string.Join(", ", candidatas.Select(s => s.Id).ToArray());

            MessageBox.Show(
              string.Format(
                "Se detectaron múltiples estructuras candidatas a CIED: {0}.\n\n" +
                "Se seleccionó automáticamente '{1}' por tener el menor volumen.\n\n" +
                "Verifique que esta selección sea la correcta antes de confiar en el reporte.",
                listaCandidatas,
                seleccionada.Id
              ),
              "Selección Automática de Estructura CIED",
              MessageBoxButton.OK,
              MessageBoxImage.Warning
            );

            WarnIfImplausibleDicomType(seleccionada);
            return seleccionada;
        }

        // Tipos DICOM que delatan que el patrón de búsqueda coincidió con algo que no es el
        // contorno de un dispositivo: un volumen blanco (p.ej. una estructura llamada "ICD boost"
        // interpretada como desfibrilador) o el contorno externo del paciente. No se valida contra
        // una lista de tipos "permitidos" porque CONTROL, ORGAN y AVOIDANCE son todos usos
        // legítimos según el flujo de contorneo de cada servicio, y alertar sobre ellos sería ruido.
        private static readonly string[] TiposImplausibles = { "PTV", "GTV", "CTV", "EXTERNAL", "BODY" };

        private void WarnIfImplausibleDicomType(Structure structure)
        {
            string tipo = structure.DicomType;

            if (string.IsNullOrEmpty(tipo))
            {
                return;
            }

            bool esImplausible = TiposImplausibles
              .Any(t => string.Equals(t, tipo, StringComparison.OrdinalIgnoreCase));

            if (!esImplausible)
            {
                return;
            }

            MessageBox.Show(
              string.Format(
                "La estructura seleccionada como CIED ('{0}') tiene un tipo DICOM de '{1}'.\n\n" +
                "Ese tipo corresponde a un volumen blanco o al contorno externo del paciente, no a un " +
                "dispositivo implantado, por lo que es probable que el patrón de búsqueda haya " +
                "coincidido con una estructura equivocada.\n\n" +
                "Verifique la estructura antes de utilizar este reporte para tomar decisiones clínicas.",
                structure.Id,
                tipo
              ),
              "Advertencia - Tipo de Estructura Inesperado",
              MessageBoxButton.OK,
              MessageBoxImage.Warning
            );
        }
    }

    // 3. CLASE AUXILIAR - FASE 2 (Extractor Dosimétrico)
    public class CiedDoseExtractor
    {
        private readonly PlanSetup _plan;
        private readonly Structure _cied;

        public double DmaxGy { get; private set; }
        public double D5PercentGy { get; private set; }

        public CiedDoseExtractor(PlanSetup plan, Structure cied)
        {
            _plan = plan;
            _cied = cied;
        }

        public void ExtractDoseData()
        {
            if (_plan.Dose == null)
            {
                throw new InvalidOperationException("La distribución de dosis no está disponible.");
            }

            // MaxDose no depende del ancho de bin (solo CurveData lo usa), así que un bin fino
            // de 0.001 Gy solo desperdicia memoria construyendo una curva que nunca se lee
            // (decenas de miles de puntos en un rango típico de dosis). 0.1 Gy es suficiente.
            DVHData dvh = _plan.GetDVHCumulativeData(_cied, DoseValuePresentation.Absolute, VolumePresentation.Relative, 0.1);

            if (dvh != null)
            {
                DmaxGy = ConvertToGy(dvh.MaxDose);
            }

            // For specific volume percentages, GetDoseAtVolume is correct
            DoseValue rawD5 = _plan.GetDoseAtVolume(_cied, 5.0, VolumePresentation.Relative, DoseValuePresentation.Absolute);
            D5PercentGy = ConvertToGy(rawD5);
        }

        // Direct property access instead of string parsing
        private double ConvertToGy(DoseValue doseValue)
        {
            if (doseValue == null) return 0.0;

            // DoseValue.Dose returns the numeric value as a double
            // DoseValue.Unit returns an enum (Gy, cGy, %, Unknown)
            if (doseValue.Unit == DoseValue.DoseUnit.cGy)
            {
                return doseValue.Dose / 100.0;
            }
            else if (doseValue.Unit == DoseValue.DoseUnit.Gy)
            {
                return doseValue.Dose;
            }
            
            // Fallback if relative or unknown
            return 0.0; 
        }
    }

    // 4a. CLASE AUXILIAR - Geometría de láminas del MLC
    //
    // ESAPI expone las posiciones de las láminas (ControlPoint.LeafPositions) pero no las fronteras
    // en Y de cada par, que dependen del modelo físico de MLC. Se derivan del identificador del
    // dispositivo contra una tabla de modelos Varian conocidos. Un modelo no reconocido devuelve
    // null y el auditor cae a evaluación por mordazas, en lugar de asumir una geometría inventada.
    public static class MlcGeometry
    {
        // Fronteras en Y (mm en el plano del isocentro), ordenadas de Y1 (negativo) a Y2 (positivo).
        // N pares de láminas producen N+1 fronteras.
        public static double[] GetLeafBoundaries(string mlcId)
        {
            if (string.IsNullOrEmpty(mlcId))
            {
                return null;
            }

            string id = mlcId.ToUpperInvariant();

            // El HD120 también contiene "120" en su nombre, así que se evalúa primero.
            if (id.Contains("HD") || id.Contains("HIGH DEFINITION"))
            {
                return BuildBoundaries(-110.0, new[] { 14, 32, 14 }, new[] { 5.0, 2.5, 5.0 });
            }

            if (id.Contains("120"))
            {
                return BuildBoundaries(-200.0, new[] { 10, 40, 10 }, new[] { 10.0, 5.0, 10.0 });
            }

            if (id.Contains("80"))
            {
                return BuildBoundaries(-200.0, new[] { 40 }, new[] { 10.0 });
            }

            return null;
        }

        private static double[] BuildBoundaries(double start, int[] counts, double[] widths)
        {
            int total = 1;
            for (int i = 0; i < counts.Length; i++)
            {
                total += counts[i];
            }

            double[] boundaries = new double[total];
            boundaries[0] = start;

            int index = 1;
            for (int seccion = 0; seccion < counts.Length; seccion++)
            {
                for (int i = 0; i < counts[seccion]; i++)
                {
                    boundaries[index] = boundaries[index - 1] + widths[seccion];
                    index++;
                }
            }

            return boundaries;
        }
    }

    // 4b. CLASE AUXILIAR - Proyección Beam's-Eye-View (IEC 61217)
    //
    // Proyecta puntos del paciente al sistema de coordenadas del haz para medir la distancia real
    // al borde del campo, considerando gantry, colimador y camilla. Reemplaza la heurística previa
    // (distancia radial al isocentro menos medio campo), que ignoraba por completo la orientación
    // del haz y por lo tanto sobrestimaba la distancia en haces que no apuntaban hacia el CIED.
    //
    // ADVERTENCIA: las convenciones de signo de rotación (especialmente colimador y camilla) están
    // documentadas explícitamente en cada bloque. Deben validarse contra casos conocidos antes de
    // usar este cálculo con fines clínicos — ver sección de validación en el README.
    public class BeamEyeViewProjector
    {
        // Distancia fuente-eje nominal de un acelerador lineal Varian, en mm.
        private const double SourceAxisDistanceMm = 1000.0;

        private readonly PatientOrientation _orientation;

        public BeamEyeViewProjector(PatientOrientation orientation)
        {
            _orientation = orientation;
        }

        public static bool IsSupportedOrientation(PatientOrientation orientation)
        {
            return orientation == PatientOrientation.HeadFirstSupine
                || orientation == PatientOrientation.HeadFirstProne
                || orientation == PatientOrientation.FeetFirstSupine
                || orientation == PatientOrientation.FeetFirstProne;
        }

        // Convierte los vértices de la malla desde coordenadas DICOM de paciente
        // (+x = izquierda, +y = posterior, +z = superior) al sistema fijo IEC 61217
        // (+Xf = derecha de un observador frente al gantry, +Yf = hacia el gantry, +Zf = arriba),
        // relativos al isocentro del haz. Se hace una sola vez por haz porque no depende del
        // control point, sólo de la orientación del paciente y del isocentro.
        //
        // Las cuatro combinaciones son transformaciones dextrógiras (determinante +1); las
        // orientaciones laterales (decúbito lateral) no están soportadas y se rechazan antes.
        public double[] ToIecFixedRelative(Point3DCollection points, VVector isocenter)
        {
            double[] resultado = new double[points.Count * 3];

            for (int i = 0; i < points.Count; i++)
            {
                Point3D p = points[i];
                double x = p.X - isocenter.x;
                double y = p.Y - isocenter.y;
                double z = p.Z - isocenter.z;

                double xf, yf, zf;

                if (_orientation == PatientOrientation.HeadFirstSupine)
                {
                    xf = x; yf = z; zf = -y;
                }
                else if (_orientation == PatientOrientation.HeadFirstProne)
                {
                    xf = -x; yf = z; zf = y;
                }
                else if (_orientation == PatientOrientation.FeetFirstSupine)
                {
                    xf = -x; yf = -z; zf = -y;
                }
                else
                {
                    xf = x; yf = -z; zf = y;
                }

                resultado[i * 3] = xf;
                resultado[i * 3 + 1] = yf;
                resultado[i * 3 + 2] = zf;
            }

            return resultado;
        }

        // Distancia mínima (mm) desde cualquier vértice del CIED hasta el borde de la apertura,
        // medida en el plano perpendicular al haz que pasa por ese vértice. Devuelve 0.0 si algún
        // vértice cae dentro de la apertura. Se evalúa para un único control point.
        //
        // Si se entregan posiciones y fronteras de láminas, la apertura es la conformada por el
        // MLC recortada por las mordazas; si no, es el rectángulo de mordazas solamente.
        public double MinDistanceOutsideFieldMm(
          double[] iecPoints,
          double gantryDeg,
          double collimatorDeg,
          double couchDeg,
          VRect<double> jaws,
          float[,] leafPositions,
          double[] leafBoundaries)
        {
            bool usarMlc = leafPositions != null
                        && leafBoundaries != null
                        && leafBoundaries.Length >= 2
                        && leafPositions.GetLength(0) == 2
                        && leafPositions.GetLength(1) == leafBoundaries.Length - 1;

            // Base del haz en el sistema fijo IEC para el ángulo de gantry dado.
            // Gantry 0 = fuente arriba, haz hacia abajo (-Zf). Gantry 90 = fuente del lado +Xf
            // (izquierda del paciente en HFS), haz viajando hacia -Xf. Coincide con la escala IEC
            // de Varian.
            double gRad = gantryDeg * Math.PI / 180.0;
            double cosG = Math.Cos(gRad);
            double sinG = Math.Sin(gRad);

            // Eje del haz, de la fuente hacia el isocentro.
            double dirX = -sinG, dirY = 0.0, dirZ = -cosG;
            // Crossplane (mordazas X) e inplane (mordazas Y) con el colimador en 0.
            double crossX = cosG, crossY = 0.0, crossZ = -sinG;
            double inplX = 0.0, inplY = 1.0, inplZ = 0.0;

            // Rotación de colimador alrededor del eje del haz. Convención asumida: ángulo positivo
            // gira el eje crossplane hacia el eje inplane. Con campos simétricos el signo es
            // irrelevante; sólo importa en campos rectangulares asimétricos.
            double cRad = collimatorDeg * Math.PI / 180.0;
            double cosC = Math.Cos(cRad);
            double sinC = Math.Sin(cRad);

            double cx = crossX * cosC + inplX * sinC;
            double cy = crossY * cosC + inplY * sinC;
            double cz = crossZ * cosC + inplZ * sinC;
            double ix = -crossX * sinC + inplX * cosC;
            double iy = -crossY * sinC + inplY * cosC;
            double iz = -crossZ * sinC + inplZ * cosC;

            // Rotación de camilla alrededor del eje vertical Zf. El paciente gira con la camilla,
            // así que se rota el punto (no el haz). Convención asumida: ángulo positivo es
            // antihorario visto desde arriba. La mayoría de los planes usan camilla 0, donde esto
            // no tiene efecto; con camilla distinta de 0 el signo debe validarse.
            double pRad = couchDeg * Math.PI / 180.0;
            double cosP = Math.Cos(pRad);
            double sinP = Math.Sin(pRad);

            // Las mordazas vienen definidas en el plano del isocentro. X1/Y1 son negativas y X2/Y2
            // positivas en ESAPI, pero se ordenan por si acaso para no invertir el intervalo.
            double xMin = Math.Min(jaws.X1, jaws.X2);
            double xMax = Math.Max(jaws.X1, jaws.X2);
            double yMin = Math.Min(jaws.Y1, jaws.Y2);
            double yMax = Math.Max(jaws.Y1, jaws.Y2);

            double minDistancia = double.MaxValue;
            int totalPuntos = iecPoints.Length / 3;

            for (int i = 0; i < totalPuntos; i++)
            {
                double xf = iecPoints[i * 3];
                double yf = iecPoints[i * 3 + 1];
                double zf = iecPoints[i * 3 + 2];

                double xr = xf * cosP - yf * sinP;
                double yr = xf * sinP + yf * cosP;
                double zr = zf;

                // Profundidad respecto al isocentro (positiva = más lejos de la fuente) y
                // coordenadas en el plano del haz.
                double depth = xr * dirX + yr * dirY + zr * dirZ;
                double cross = xr * cx + yr * cy + zr * cz;
                double inpl = xr * ix + yr * iy + zr * iz;

                // El campo diverge con la distancia a la fuente, así que el borde se escala del
                // plano del isocentro al plano donde está realmente el punto.
                double divergencia = (SourceAxisDistanceMm + depth) / SourceAxisDistanceMm;
                if (divergencia < 0.0)
                {
                    // Punto detrás de la fuente: geometría degenerada, no aporta información útil.
                    continue;
                }

                double outCross = 0.0;
                if (cross < xMin * divergencia) outCross = xMin * divergencia - cross;
                else if (cross > xMax * divergencia) outCross = cross - xMax * divergencia;

                double outInpl = 0.0;
                if (inpl < yMin * divergencia) outInpl = yMin * divergencia - inpl;
                else if (inpl > yMax * divergencia) outInpl = inpl - yMax * divergencia;

                double distanciaMordazas = Math.Sqrt(outCross * outCross + outInpl * outInpl);

                if (!usarMlc)
                {
                    if (distanciaMordazas <= 0.0)
                    {
                        return 0.0;
                    }

                    if (distanciaMordazas < minDistancia)
                    {
                        minDistancia = distanciaMordazas;
                    }

                    continue;
                }

                // La apertura del MLC está contenida en el rectángulo de mordazas, así que la
                // distancia al MLC nunca puede ser menor que la distancia a las mordazas. Si esta
                // última ya no mejora el mínimo acumulado, el cálculo por láminas es innecesario.
                if (distanciaMordazas >= minDistancia)
                {
                    continue;
                }

                double distanciaApertura = DistanceToMlcApertureMm(
                  cross, inpl, divergencia, xMin, xMax, yMin, yMax,
                  leafPositions, leafBoundaries, minDistancia
                );

                if (distanciaApertura <= 0.0)
                {
                    return 0.0;
                }

                if (distanciaApertura < minDistancia)
                {
                    minDistancia = distanciaApertura;
                }
            }

            return minDistancia == double.MaxValue ? 0.0 : minDistancia;
        }

        // La apertura conformada por el MLC es la unión de un rectángulo por par de láminas
        // (el hueco entre las dos láminas, limitado en Y por las fronteras del par y recortado por
        // las mordazas). La distancia a una unión es el mínimo de las distancias, así que basta
        // recorrer los pares. Los pares cerrados o completamente tapados por mordazas no aportan.
        private static double DistanceToMlcApertureMm(
          double cross,
          double inpl,
          double divergencia,
          double xMin,
          double xMax,
          double yMin,
          double yMax,
          float[,] leafPositions,
          double[] leafBoundaries,
          double mejorConocida)
        {
            double jawXMin = xMin * divergencia;
            double jawXMax = xMax * divergencia;
            double jawYMin = yMin * divergencia;
            double jawYMax = yMax * divergencia;

            double mejor = mejorConocida;
            int totalPares = leafBoundaries.Length - 1;

            for (int par = 0; par < totalPares; par++)
            {
                double rectYMin = Math.Max(leafBoundaries[par] * divergencia, jawYMin);
                double rectYMax = Math.Min(leafBoundaries[par + 1] * divergencia, jawYMax);

                if (rectYMax <= rectYMin)
                {
                    // Par completamente fuera de la apertura de mordazas.
                    continue;
                }

                // Poda: la distancia sólo en Y ya acota por debajo la distancia al rectángulo, así
                // que un par demasiado alejado en Y no puede mejorar el mínimo.
                double distY = 0.0;
                if (inpl < rectYMin) distY = rectYMin - inpl;
                else if (inpl > rectYMax) distY = inpl - rectYMax;

                if (distY >= mejor)
                {
                    continue;
                }

                // Banco 0 = lado X1 (negativo), banco 1 = lado X2 (positivo).
                double rectXMin = Math.Max(leafPositions[0, par] * divergencia, jawXMin);
                double rectXMax = Math.Min(leafPositions[1, par] * divergencia, jawXMax);

                if (rectXMax <= rectXMin)
                {
                    // Par de láminas cerrado, o su hueco queda fuera de las mordazas.
                    continue;
                }

                double distX = 0.0;
                if (cross < rectXMin) distX = rectXMin - cross;
                else if (cross > rectXMax) distX = cross - rectXMax;

                double distancia = Math.Sqrt(distX * distX + distY * distY);

                if (distancia <= 0.0)
                {
                    return 0.0;
                }

                if (distancia < mejor)
                {
                    mejor = distancia;
                }
            }

            return mejor;
        }
    }

    // 4. CLASE AUXILIAR - FASE 3 (Auditor de Haces y Distancias)
    public class CiedBeamAuditor
    {
        private readonly PlanSetup _plan;
        private readonly Structure _cied;

        public bool HasHighEnergyRisk { get; private set; }
        public string MaxEnergyName { get; private set; }
        public double MinDistanceToEdgeCm { get; private set; }

        // Falso si ningún haz de tratamiento aportó geometría evaluable. Permite distinguir
        // "no se pudo medir" de "el dispositivo está dentro del campo", que son 0.0 cm ambos.
        public bool HasGeometryData { get; private set; }

        // Haz y ángulos del control point donde se alcanza la distancia mínima, para que el
        // físico pueda ir directamente a esa geometría en Eclipse y verificarla.
        public string MinDistanceBeamId { get; private set; }
        public double MinDistanceGantryAngle { get; private set; }
        public double MinDistanceCollimatorAngle { get; private set; }
        public double MinDistanceCouchAngle { get; private set; }

        // Cuántos control points dejan el dispositivo dentro del campo, sobre el total evaluado.
        // Con distancia 0.0 el ángulo del mínimo no es único, así que el conteo dice si se trata
        // de un instante puntual del arco o de una fracción sustancial del tratamiento.
        public int InFieldControlPointCount { get; private set; }
        public int EvaluatedControlPointCount { get; private set; }

        // Describe con qué nivel de detalle se calculó la geometría, para que el reporte no
        // presente un resultado por mordazas como si hubiese considerado el bloqueo del MLC.
        public string ApertureModelDescription { get; private set; }

        public CiedBeamAuditor(PlanSetup plan, Structure cied)
        {
            _plan = plan;
            _cied = cied;
            HasHighEnergyRisk = false;
            MaxEnergyName = "Desconocida";
            MinDistanceToEdgeCm = 999.0;
            HasGeometryData = false;
            MinDistanceBeamId = "";
            ApertureModelDescription = "";
        }

        public void AuditBeams()
        {
            int maxEnergyFound = 0;

            if (_cied.MeshGeometry == null)
            {
                throw new InvalidOperationException(
                  "La estructura CIED no tiene una geometría de malla válida (contorno incompleto o vacío)."
                );
            }

            PatientOrientation orientation = _plan.TreatmentOrientation;

            if (!BeamEyeViewProjector.IsSupportedOrientation(orientation))
            {
                throw new InvalidOperationException(
                  string.Format(
                    "La orientación de tratamiento '{0}' no está soportada por el cálculo geométrico " +
                    "beam's-eye-view. Sólo se admiten HeadFirstSupine, HeadFirstProne, FeetFirstSupine " +
                    "y FeetFirstProne.",
                    orientation
                  )
                );
            }

            BeamEyeViewProjector projector = new BeamEyeViewProjector(orientation);
            Point3DCollection meshPoints = _cied.MeshGeometry.Positions;

            foreach (Beam beam in _plan.Beams)
            {
                // Use native API property instead of string matching
                if (beam.IsSetupField)
                {
                    continue;
                }

                if (beam.EnergyMode != null)
                {
                    string energyId = beam.EnergyMode.Id;

                    Match matchEnergia = Regex.Match(energyId, @"\d+");
                    if (matchEnergia.Success)
                    {
                        int valorEnergia = Convert.ToInt32(matchEnergia.Value);

                        // Solo se actualiza el nombre reportado cuando el haz es realmente el de
                        // mayor energía vista hasta el momento; antes se sobrescribía en cada
                        // iteración y el reporte terminaba mostrando el último haz, no el máximo.
                        if (valorEnergia > maxEnergyFound)
                        {
                            maxEnergyFound = valorEnergia;
                            MaxEnergyName = energyId;
                        }

                        if (valorEnergia >= 10)
                        {
                            HasHighEnergyRisk = true;
                        }
                    }
                }

                if (beam.ControlPoints == null || beam.ControlPoints.Count == 0)
                {
                    continue;
                }

                // No se corta al llegar a 0.0 cm: recorrer el arco completo permite contar en
                // cuántos control points el dispositivo queda dentro del campo, que distingue un
                // instante puntual de una fracción sustancial del tratamiento. El coste es bajo
                // porque MinDistanceOutsideFieldMm retorna en cuanto encuentra un vértice dentro.

                // La conversión al sistema IEC sólo depende de la orientación y del isocentro, no
                // del control point, así que se hace una vez por haz y se reutiliza en todos.
                double[] iecPoints = projector.ToIecFixedRelative(meshPoints, beam.IsocenterPosition);

                // Las fronteras de las láminas dependen del modelo de MLC, no del control point.
                string mlcId = beam.MLC != null ? beam.MLC.Id : null;
                double[] leafBoundaries = MlcGeometry.GetLeafBoundaries(mlcId);
                RecordApertureModel(mlcId, leafBoundaries);

                // En VMAT el gantry (y las mordazas) cambian en cada control point, así que evaluar
                // sólo el primero subestimaba groseramente el riesgo: basta con que un ángulo del
                // arco apunte al CIED para que la distancia real sea cero.
                HasGeometryData = true;

                foreach (ControlPoint controlPoint in beam.ControlPoints)
                {
                    double distanciaMm = projector.MinDistanceOutsideFieldMm(
                      iecPoints,
                      controlPoint.GantryAngle,
                      controlPoint.CollimatorAngle,
                      controlPoint.PatientSupportAngle,
                      controlPoint.JawPositions,
                      leafBoundaries != null ? controlPoint.LeafPositions : null,
                      leafBoundaries
                    );

                    double distanciaCm = distanciaMm / 10.0;
                    EvaluatedControlPointCount++;

                    if (distanciaCm <= 0.0)
                    {
                        InFieldControlPointCount++;
                    }

                    if (distanciaCm < MinDistanceToEdgeCm)
                    {
                        MinDistanceToEdgeCm = distanciaCm;
                        MinDistanceBeamId = beam.Id;
                        MinDistanceGantryAngle = controlPoint.GantryAngle;
                        MinDistanceCollimatorAngle = controlPoint.CollimatorAngle;
                        MinDistanceCouchAngle = controlPoint.PatientSupportAngle;
                    }
                }
            }

            if (!HasGeometryData)
            {
                MinDistanceToEdgeCm = 0.0;
            }
        }

        // Deja constancia del modelo de apertura efectivamente usado. Si distintos haces usan
        // distintos MLC (poco habitual pero posible en un plan mixto), prevalece la descripción
        // menos precisa: el reporte debe reflejar el eslabón más débil del cálculo.
        private void RecordApertureModel(string mlcId, double[] leafBoundaries)
        {
            string descripcion;

            if (leafBoundaries != null)
            {
                descripcion = string.Format("mordazas + MLC ({0}, {1} pares de láminas)", mlcId, leafBoundaries.Length - 1);
            }
            else if (string.IsNullOrEmpty(mlcId))
            {
                descripcion = "sólo mordazas (el haz no declara MLC)";
            }
            else
            {
                descripcion = string.Format(
                  "sólo mordazas — modelo de MLC '{0}' no reconocido, el bloqueo por láminas NO fue evaluado",
                  mlcId
                );
            }

            bool yaEsDegradado = ApertureModelDescription.StartsWith("sólo mordazas");

            if (ApertureModelDescription.Length == 0 || (!yaEsDegradado && leafBoundaries == null))
            {
                ApertureModelDescription = descripcion;
            }
        }
    }

    // 5. CLASE AUXILIAR - FASE 4 (Motor Lógico de Riesgo)
    public class CiedRiskEvaluator
    {
        // Umbrales según AAPM TG-203, centralizados aquí para que la clasificación global y los
        // semáforos por ítem del reporte visual usen el mismo origen y no se desincronicen.
        public const double DoseAltoRiesgoGy = 5.0;
        public const double DoseModeradoMinGy = 2.0;
        public const double DistanciaCriticaCm = 5.0;

        private readonly double _dmax;
        private readonly bool _hasNeutrons;
        private readonly double _distance;
        private readonly bool _hasGeometryData;

        public string RiskLevel { get; private set; }
        public string RiskLevelTier { get; private set; }
        public string Recommendation { get; private set; }

        public string DoseRiskLevel { get; private set; }
        public string EnergyRiskLevel { get; private set; }
        public string DistanceRiskLevel { get; private set; }

        public CiedRiskEvaluator(double dmax, bool hasNeutrons, double distance, bool hasGeometryData)
        {
            _dmax = dmax;
            _hasNeutrons = hasNeutrons;
            _distance = distance;
            _hasGeometryData = hasGeometryData;
            RiskLevel = "Bajo Riesgo";
            RiskLevelTier = "Bajo";
            Recommendation = "";
        }

        public void EvaluateRisk()
        {
            ClassifyDose();
            ClassifyEnergyAndDistance();

            // El veredicto global se deriva de los mismos niveles por ítem que se muestran en
            // el reporte visual, en vez de re-evaluar los umbrales por separado.
            if (DoseRiskLevel == "Alto" || EnergyRiskLevel == "Alto")
            {
                RiskLevelTier = "Alto";
                RiskLevel = "Alto Riesgo";
                Recommendation = "- Requiere re-planificación inmediata si es físicamente posible.\n- Monitorización cardíaca continua por ECG durante cada fracción.\n- Interrogación del CIED antes de la primera sesión y al finalizar el tratamiento.";
            }
            else if (DoseRiskLevel == "Moderado" || EnergyRiskLevel == "Moderado" || DistanceRiskLevel == "Moderado")
            {
                RiskLevelTier = "Moderado";
                RiskLevel = "Riesgo Moderado";
                Recommendation = "- Verificar la dosis acumulada semanalmente.\n- Considerar reprogramación/interrogación del dispositivo a mitad del tratamiento.\n- Monitorización de signos vitales básicos en sala.";
            }
            else
            {
                RiskLevelTier = "Bajo";
                RiskLevel = "Bajo Riesgo";
                Recommendation = "- Nivel seguro. Proceder con el tratamiento estándar.\n- Realizar una interrogación de control post-radioterapia por protocolo.";
            }
        }

        private void ClassifyDose()
        {
            if (_dmax > DoseAltoRiesgoGy)
            {
                DoseRiskLevel = "Alto";
            }
            else if (_dmax >= DoseModeradoMinGy)
            {
                DoseRiskLevel = "Moderado";
            }
            else
            {
                DoseRiskLevel = "Bajo";
            }
        }

        private void ClassifyEnergyAndDistance()
        {
            if (!_hasGeometryData)
            {
                // Sin haces de tratamiento con control points no hay geometría que clasificar.
                // Se deja la distancia sin nivel (punto gris) en lugar de inventar un 0.0 que
                // el clasificador leería como un dato real.
                EnergyRiskLevel = _hasNeutrons ? "Moderado" : "Bajo";
                DistanceRiskLevel = null;
                return;
            }

            // Una distancia de 0.0 cm significa que el dispositivo queda dentro del campo en algún
            // ángulo: es el caso extremo de "distancia por debajo del umbral crítico", no un caso
            // seguro. Antes quedaba excluido por un guard `distancia > 0` heredado de cuando 0.0
            // sólo señalaba ausencia de datos, lo que hacía que un CIED dentro del campo se
            // reportara en verde.
            bool distanciaBajoUmbral = _distance < DistanciaCriticaCm;

            // La contaminación por neutrones solo es clínicamente relevante si el CIED está
            // cerca del campo, así que energía y distancia se evalúan como una regla acoplada,
            // igual que en la lógica original de este motor de riesgo.
            if (_hasNeutrons && distanciaBajoUmbral)
            {
                EnergyRiskLevel = "Alto";
                DistanceRiskLevel = "Alto";
            }
            else if (_hasNeutrons)
            {
                EnergyRiskLevel = "Moderado";
                DistanceRiskLevel = "Moderado";
            }
            else if (distanciaBajoUmbral)
            {
                EnergyRiskLevel = "Bajo";
                DistanceRiskLevel = "Moderado";
            }
            else
            {
                EnergyRiskLevel = "Bajo";
                DistanceRiskLevel = "Bajo";
            }
        }
    }

    // 6. CLASE AUXILIAR - UI (Ventana de Reporte Visual con Semáforos)
    public class CiedAuditReportWindow : Window
    {
        private static readonly Brush ColorAlto = new SolidColorBrush(Color.FromRgb(0xD9, 0x2D, 0x20));
        private static readonly Brush ColorModerado = new SolidColorBrush(Color.FromRgb(0xE8, 0xA6, 0x0D));
        private static readonly Brush ColorBajo = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x4B));
        private static readonly Brush ColorInformativo = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

        public CiedAuditReportWindow(
          Structure detectedCied,
          CiedDoseExtractor doseExtractor,
          CiedBeamAuditor beamAuditor,
          CiedRiskEvaluator riskEvaluator)
        {
            Title = "Reporte de Auditoría Clínica - Final";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            MinWidth = 480;

            StackPanel mainStack = new StackPanel { Margin = new Thickness(18) };

            mainStack.Children.Add(new TextBlock
            {
                Text = "AUDITORÍA DE SEGURIDAD DE CIEDs (TG-203)",
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Margin = new Thickness(0, 0, 0, 4)
            });

            mainStack.Children.Add(new TextBlock
            {
                Text = string.Format("Dispositivo Detectado: {0}    |    Tipo de Volumen: {1}", detectedCied.Id, detectedCied.DicomType),
                Margin = new Thickness(0, 0, 0, 14)
            });

            mainStack.Children.Add(BuildItemRow(
              "Dosis Máxima (Dmax)",
              string.Format("{0:F3} Gy", doseExtractor.DmaxGy),
              string.Format(
                "Bajo < {0:F1} Gy   |   Moderado {0:F1}-{1:F1} Gy   |   Alto > {1:F1} Gy",
                CiedRiskEvaluator.DoseModeradoMinGy,
                CiedRiskEvaluator.DoseAltoRiesgoGy
              ),
              riskEvaluator.DoseRiskLevel
            ));

            mainStack.Children.Add(BuildItemRow(
              "Dosis en Volumen (D5%)",
              string.Format("{0:F3} Gy", doseExtractor.D5PercentGy),
              "Valor informativo — no tiene un umbral de riesgo propio en este motor de evaluación.",
              null
            ));

            mainStack.Children.Add(BuildItemRow(
              "Energía / Contaminación por Neutrones (>=10MV)",
              string.Format("{0}  ({1})", beamAuditor.MaxEnergyName, beamAuditor.HasHighEnergyRisk ? "detectada" : "no detectada"),
              string.Format(
                "Alto si hay neutrones y distancia < {0:F0}cm   |   Moderado si hay neutrones y distancia >= {0:F0}cm   |   Bajo si no hay neutrones",
                CiedRiskEvaluator.DistanciaCriticaCm
              ),
              riskEvaluator.EnergyRiskLevel
            ));

            string distanciaValor;

            if (!beamAuditor.HasGeometryData)
            {
                distanciaValor = "Sin datos — ningún haz de tratamiento con geometría evaluable";
            }
            else if (beamAuditor.MinDistanceToEdgeCm <= 0.0)
            {
                distanciaValor = string.Format(
                  "0.0 cm — DENTRO de la apertura en {0} de {1} control points\n" +
                  "Primera entrada: {2}, gantry {3:F1}° / colimador {4:F1}° / camilla {5:F1}°",
                  beamAuditor.InFieldControlPointCount,
                  beamAuditor.EvaluatedControlPointCount,
                  beamAuditor.MinDistanceBeamId,
                  beamAuditor.MinDistanceGantryAngle,
                  beamAuditor.MinDistanceCollimatorAngle,
                  beamAuditor.MinDistanceCouchAngle
                );
            }
            else
            {
                distanciaValor = string.Format(
                  "{0:F1} cm\nMínimo en: {1}, gantry {2:F1}° / colimador {3:F1}° / camilla {4:F1}°",
                  beamAuditor.MinDistanceToEdgeCm,
                  beamAuditor.MinDistanceBeamId,
                  beamAuditor.MinDistanceGantryAngle,
                  beamAuditor.MinDistanceCollimatorAngle,
                  beamAuditor.MinDistanceCouchAngle
                );
            }

            string umbralDistancia = string.Format(
              "Alto si < {0:F0}cm con neutrones   |   Moderado si < {0:F0}cm sin neutrones, o >= {0:F0}cm con neutrones   |   Bajo si >= {0:F0}cm sin neutrones",
              CiedRiskEvaluator.DistanciaCriticaCm
            );

            if (beamAuditor.HasGeometryData)
            {
                umbralDistancia += "\nApertura evaluada: " + beamAuditor.ApertureModelDescription;
            }

            mainStack.Children.Add(BuildItemRow(
              "Distancia Mínima al Borde de Campo (BEV, todos los ángulos)",
              distanciaValor,
              umbralDistancia,
              riskEvaluator.DistanceRiskLevel
            ));

            mainStack.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });

            StackPanel overallPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            overallPanel.Children.Add(CreateDot(riskEvaluator.RiskLevelTier, 22));
            overallPanel.Children.Add(new TextBlock
            {
                Text = "  CATEGORÍA DE RIESGO: " + riskEvaluator.RiskLevel.ToUpper(),
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            });
            mainStack.Children.Add(overallPanel);

            mainStack.Children.Add(new TextBlock { Text = "Acción Recomendada:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
            mainStack.Children.Add(new TextBlock { Text = riskEvaluator.Recommendation, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });

            Button botonAceptar = new Button { Content = "Aceptar", Width = 100, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right };
            botonAceptar.Click += (sender, args) => Close();
            mainStack.Children.Add(botonAceptar);

            Content = mainStack;
        }

        private UIElement BuildItemRow(string etiqueta, string valor, string textoUmbral, string nivel)
        {
            Grid grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Ellipse dot = CreateDot(nivel, 14);
            Grid.SetColumn(dot, 0);

            StackPanel textPanel = new StackPanel();
            textPanel.Children.Add(new TextBlock { Text = etiqueta, FontWeight = FontWeights.SemiBold });
            textPanel.Children.Add(new TextBlock { Text = valor, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            textPanel.Children.Add(new TextBlock
            {
                Text = textoUmbral,
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(textPanel, 1);

            grid.Children.Add(dot);
            grid.Children.Add(textPanel);
            return grid;
        }

        // nivel: "Alto" / "Moderado" / "Bajo", o null para un ítem sin umbral de riesgo (gris).
        private Ellipse CreateDot(string nivel, double tamano)
        {
            Brush color;

            if (nivel == "Alto")
            {
                color = ColorAlto;
            }
            else if (nivel == "Moderado")
            {
                color = ColorModerado;
            }
            else if (nivel == "Bajo")
            {
                color = ColorBajo;
            }
            else
            {
                color = ColorInformativo;
            }

            return new Ellipse
            {
                Width = tamano,
                Height = tamano,
                Fill = color,
                Margin = new Thickness(2, 4, 8, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
        }
    }
}