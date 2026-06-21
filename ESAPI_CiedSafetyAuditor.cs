using System;
using System.Linq;
using System.Text;
using System.Windows;
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
                      beamAuditor.MinDistanceToEdgeCm
                    );
                    riskEvaluator.EvaluateRisk();

                    // REPORTE CLÍNICO CONSOLIDADO FINAL
                    string reporteFinal = string.Format(
                      "=========================================\n" +
                      "   AUDITORÍA DE SEGURIDAD DE CIEDs (TG-203)   \n" +
                      "=========================================\n" +
                      "Dispositivo Detectado: {0}\n" +
                      "Tipo de Volumen: {1}\n\n" +
                      "1. ANÁLISIS DOSIMÉTRICO:\n" +
                      " - Dosis Máxima (Dmax): {2:F3} Gy\n" +
                      " - Dosis en Volumen (D5%): {3:F3} Gy\n\n" +
                      "2. ANÁLISIS DE HAZ Y GEOMETRÍA:\n" +
                      " - Energía Máxima: {4}\n" +
                      " - Contaminación por Neutrones (>=10MV): {5}\n" +
                      " - Distancia Mínima Estimada al Borde: {6:F1} cm\n\n" +
                      "=========================================\n" +
                      "   CATEGORÍA DE RIESGO: {7}\n" +
                      "=========================================\n" +
                      "Acción Recomendada:\n{8}",
                      detectedCied.Id,
                      detectedCied.DicomType,
                      doseExtractor.DmaxGy,
                      doseExtractor.D5PercentGy,
                      beamAuditor.MaxEnergyName,
                      beamAuditor.HasHighEnergyRisk ? "SÍ (Alto Riesgo)" : "No detectada (Seguro)",
                      beamAuditor.MinDistanceToEdgeCm,
                      riskEvaluator.RiskLevel.ToUpper(),
                      riskEvaluator.Recommendation
                    );

                    MessageBox.Show(reporteFinal, "Reporte de Auditoría Clínica - Final", MessageBoxButton.OK, MessageBoxImage.Information);
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

        public Structure FindCiedStructure()
        {
            string patronBusqueda = @"(cied|marcapaso|pacemaker|icd|desfibrilador|generador)";
            Regex regexCied = new Regex(patronBusqueda, RegexOptions.IgnoreCase);

            foreach (Structure structure in _context.StructureSet.Structures)
            {
                if (structure.IsEmpty)
                {
                    continue;
                }

                if (regexCied.IsMatch(structure.Id))
                {
                    return structure;
                }
            }

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

            // Use GetDVHCumulativeData for Max/Mean doses
            DVHData dvh = _plan.GetDVHCumulativeData(_cied, DoseValuePresentation.Absolute, VolumePresentation.Relative, 0.001);
            
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

    // 4. CLASE AUXILIAR - FASE 3 (Auditor de Haces y Distancias)
    public class CiedBeamAuditor
    {
        private readonly PlanSetup _plan;
        private readonly Structure _cied;

        public bool HasHighEnergyRisk { get; private set; }
        public string MaxEnergyName { get; private set; }
        public double MinDistanceToEdgeCm { get; private set; }

        public CiedBeamAuditor(PlanSetup plan, Structure cied)
        {
            _plan = plan;
            _cied = cied;
            HasHighEnergyRisk = false;
            MaxEnergyName = "Desconocida";
            MinDistanceToEdgeCm = 999.0;
        }

        public void AuditBeams()
        {
            int maxEnergyFound = 0;

            // MeshGeometry.Bounds gives the 3D bounding box
            var bounds = _cied.MeshGeometry.Bounds;
            double ciedX = bounds.X + (bounds.SizeX / 2.0);
            double ciedY = bounds.Y + (bounds.SizeY / 2.0);
            double ciedZ = bounds.Z + (bounds.SizeZ / 2.0);

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
                    MaxEnergyName = energyId;

                    Match matchEnergia = Regex.Match(energyId, @"\d+");
                    if (matchEnergia.Success)
                    {
                        int valorEnergia = Convert.ToInt32(matchEnergia.Value);
                        if (valorEnergia > maxEnergyFound)
                        {
                            maxEnergyFound = valorEnergia;
                        }

                        if (valorEnergia >= 10)
                        {
                            HasHighEnergyRisk = true;
                        }
                    }
                }

                VVector beamIsocenter = beam.IsocenterPosition;

                // Isocenter positions in ESAPI are in millimeters. Dividing by 10 converts to cm.
                double deltaX = (ciedX - beamIsocenter.x) / 10.0;
                double deltaY = (ciedY - beamIsocenter.y) / 10.0;
                double deltaZ = (ciedZ - beamIsocenter.z) / 10.0;

                double distanceToIsocenter = Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);

                // Geometric heuristic: subtract approximate half-field size (5cm)
                double fieldApproximation = distanceToIsocenter - 5.0;
                if (fieldApproximation < 0.0) fieldApproximation = 0.0;

                if (fieldApproximation < MinDistanceToEdgeCm)
                {
                    MinDistanceToEdgeCm = fieldApproximation;
                }
            }

            if (MinDistanceToEdgeCm == 999.0)
            {
                MinDistanceToEdgeCm = 0.0;
            }
        }
    }

    // 5. CLASE AUXILIAR - FASE 4 (Motor Lógico de Riesgo)
    public class CiedRiskEvaluator
    {
        private readonly double _dmax;
        private readonly bool _hasNeutrons;
        private readonly double _distance;

        public string RiskLevel { get; private set; }
        public string Recommendation { get; private set; }

        public CiedRiskEvaluator(double dmax, bool hasNeutrons, double distance)
        {
            _dmax = dmax;
            _hasNeutrons = hasNeutrons;
            _distance = distance;
            RiskLevel = "Bajo";
            Recommendation = "";
        }

        public void EvaluateRisk()
        {
            // Regla de ALTO RIESGO según AAPM TG-203
            if (_dmax > 5.0 || (_hasNeutrons && _distance < 5.0))
            {
                RiskLevel = "Alto Riesgo";
                Recommendation = "- Requiere re-planificación inmediata si es físicamente posible.\n- Monitorización cardíaca continua por ECG durante cada fracción.\n- Interrogación del CIED antes de la primera sesión y al finalizar el tratamiento.";
            }
            // Regla de RIESGO MODERADO
            else if ((_dmax >= 2.0 && _dmax <= 5.0) || (_hasNeutrons && _distance >= 5.0) || (_distance > 0.0 && _distance < 5.0))
            {
                RiskLevel = "Riesgo Moderado";
                Recommendation = "- Verificar la dosis acumulada semanalmente.\n- Considerar reprogramación/interrogación del dispositivo a mitad del tratamiento.\n- Monitorización de signos vitales básicos en sala.";
            }
            // Regla de BAJO RIESGO
            else
            {
                RiskLevel = "Bajo Riesgo";
                Recommendation = "- Nivel seguro. Proceder con el tratamiento estándar.\n- Realizar una interrogación de control post-radioterapia por protocolo.";
            }
        }
    }
}