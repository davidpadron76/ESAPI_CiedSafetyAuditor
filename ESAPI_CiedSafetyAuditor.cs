using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
                      beamAuditor.MinDistanceToEdgeCm
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

            return seleccionada;
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

            if (_cied.MeshGeometry == null)
            {
                throw new InvalidOperationException(
                  "La estructura CIED no tiene una geometría de malla válida (contorno incompleto o vacío)."
                );
            }

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

                VVector beamIsocenter = beam.IsocenterPosition;

                // Isocenter positions in ESAPI are in millimeters. Dividing by 10 converts to cm.
                double deltaX = (ciedX - beamIsocenter.x) / 10.0;
                double deltaY = (ciedY - beamIsocenter.y) / 10.0;
                double deltaZ = (ciedZ - beamIsocenter.z) / 10.0;

                double distanceToIsocenter = Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);

                // Geometric heuristic: usar el tamaño real de campo (jaws) del primer control
                // point en vez de un valor fijo de 10x10cm. Se toma la mayor distancia jaw-a-eje
                // de los cuatro bordes (X1, X2, Y1, Y2) para no subestimar el alcance del campo en
                // colimación asimétrica; sigue sin considerar gantry/couch (ver limitación conocida),
                // pero refleja la apertura configurada en vez de asumirla. Si no hay control points
                // o jaws disponibles (p.ej. aplicador de electrones no estándar), se conserva el
                // valor de respaldo de 5cm usado originalmente.
                double fieldHalfSizeCm = 5.0;
                ControlPoint firstControlPoint = beam.ControlPoints != null ? beam.ControlPoints.FirstOrDefault() : null;

                if (firstControlPoint != null)
                {
                    var jaws = firstControlPoint.JawPositions;
                    double maxReachMm = new[] { Math.Abs(jaws.X1), Math.Abs(jaws.X2), Math.Abs(jaws.Y1), Math.Abs(jaws.Y2) }.Max();

                    if (maxReachMm > 0.0)
                    {
                        fieldHalfSizeCm = maxReachMm / 10.0;
                    }
                }

                double fieldApproximation = distanceToIsocenter - fieldHalfSizeCm;
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
        // Umbrales según AAPM TG-203, centralizados aquí para que la clasificación global y los
        // semáforos por ítem del reporte visual usen el mismo origen y no se desincronicen.
        public const double DoseAltoRiesgoGy = 5.0;
        public const double DoseModeradoMinGy = 2.0;
        public const double DistanciaCriticaCm = 5.0;

        private readonly double _dmax;
        private readonly bool _hasNeutrons;
        private readonly double _distance;

        public string RiskLevel { get; private set; }
        public string RiskLevelTier { get; private set; }
        public string Recommendation { get; private set; }

        public string DoseRiskLevel { get; private set; }
        public string EnergyRiskLevel { get; private set; }
        public string DistanceRiskLevel { get; private set; }

        public CiedRiskEvaluator(double dmax, bool hasNeutrons, double distance)
        {
            _dmax = dmax;
            _hasNeutrons = hasNeutrons;
            _distance = distance;
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
            // La contaminación por neutrones solo es clínicamente relevante si el CIED está
            // cerca del campo, así que energía y distancia se evalúan como una regla acoplada,
            // igual que en la lógica original de este motor de riesgo.
            bool distanciaMenorCritica = _distance < DistanciaCriticaCm;
            bool distanciaEnRangoModeradoSinNeutrones = _distance > 0.0 && _distance < DistanciaCriticaCm;

            if (_hasNeutrons && distanciaMenorCritica)
            {
                EnergyRiskLevel = "Alto";
                DistanceRiskLevel = "Alto";
            }
            else if (_hasNeutrons)
            {
                EnergyRiskLevel = "Moderado";
                DistanceRiskLevel = "Moderado";
            }
            else if (distanciaEnRangoModeradoSinNeutrones)
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

            mainStack.Children.Add(BuildItemRow(
              "Distancia Mínima Estimada al Borde",
              string.Format("{0:F1} cm", beamAuditor.MinDistanceToEdgeCm),
              string.Format(
                "Alto si < {0:F0}cm con neutrones   |   Moderado si < {0:F0}cm sin neutrones, o >= {0:F0}cm con neutrones   |   Bajo si >= {0:F0}cm sin neutrones",
                CiedRiskEvaluator.DistanciaCriticaCm
              ),
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
            textPanel.Children.Add(new TextBlock { Text = valor, FontSize = 13 });
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