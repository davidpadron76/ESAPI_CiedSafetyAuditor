# ESAPI_CiedSafetyAuditor

`ESAPI_CiedSafetyAuditor` es un script binario de automatización (*Binary Plug-in*) desarrollado en C# para la API de Eclipse (ESAPI) de Varian Medical Systems. Su propósito es actuar como una barrera de control proactiva y automatizada para la gestión de riesgos y protección radiológica en pacientes portadores de Dispositivos Electrónicos Cardíacos Implantables (CIEDs), tales como marcapasos y desfibriladores (ICD), siguiendo las recomendaciones internacionales del reporte **AAPM TG-203**.

Este proyecto fue seleccionado para **Presentación Oral** en el *XIII Congreso Regional de Seguridad Radiológica y Nuclear (XI Congreso Regional IRPA 2026)* en Medellín, Colombia.

## 🚀 Características y Arquitectura por Fases

El script está diseñado bajo un enfoque modular y unificado en un único archivo para maximizar la portabilidad y asegurar la retrocompatibilidad con entornos clínicos que utilicen versiones previas de la API (compatibilidad estricta con C# 5.0 y remoción de interpolación de cadenas):

* **Fase 1: Módulo de Contexto y Detección Estructural:** Identificación algorítmica del contorno del dispositivo en el `StructureSet` activo utilizando expresiones regulares (`RegEx`) insensibles a mayúsculas/minúsculas (`CIED`, `Marcapasos`, `ICD`, `Desfibrilador`). Si el patrón coincide con varias estructuras (por ejemplo `CIED` y `CIED_PRV` en el mismo plan), se selecciona automáticamente la de menor volumen y se advierte al usuario sobre las candidatas detectadas, evitando depender del orden no garantizado de `StructureSet.Structures`.
* **Fase 2: Extracción Dosimétrica:** Interrogación directa de la distribución de dosis de Eclipse para aislar la Dosis Máxima (**Dmax**, vía `DVHData.MaxDose`, independiente de la resolución de bin) y la dosis en volumen (**D5%**, vía `GetDoseAtVolume`), sin depender de la interpolación de la curva DVH acumulada.
* **Fase 3: Auditoría de Haces y Análisis Geométrico:** Inspección automatizada de las energías del plan para identificar umbrales críticos de contaminación por neutrones fotonucleares (mayor o igual a **10 MV**) y cálculo de la distancia euclidiana periférica mínima entre el isocentro de cada haz y los límites de la malla estructural (`MeshGeometry.Bounds`), restando el tamaño real de campo obtenido de las posiciones de jaws (`ControlPoint.JawPositions`) del primer control point.
* **Fase 4: Motor Clínico de Evaluación de Riesgo:** Clasificación algorítmica del nivel de riesgo del paciente (Bajo, Moderado, Alto) cruzando dosis, energía y distancia, desplegando de forma inmediata las barreras de control y recomendaciones médicas sugeridas por el TG-203.

## ⚠️ Limitaciones Conocidas

* El cálculo de distancia al borde del campo usa el isocentro y el tamaño de jaws del haz, pero no proyecta la geometría a través de la rotación de gantry, colimador o camilla — es una aproximación geométrica simplificada, no un cálculo *beam's-eye-view* completo.
* La detección de neutrones por fotoactivación (≥10 MV) no distingue entre modalidad de fotones y electrones; se basa únicamente en el valor numérico de energía del haz.

## 🛠️ Requisitos e Instalación

* **Entorno:** Eclipse Treatment Planning System (Varian Medical Systems).
* **Compatibilidad de API:** Verificado en librerías nativas `VMS.TPS.Common.Model.API` y `VMS.TPS.Common.Model.Types`.
* **Compilación:** Microsoft Visual Studio (Class Library .NET Framework).

### Instrucciones:
1. Clonar el repositorio privado.
2. Abrir la solución `.sln` en Visual Studio.
3. Compilar el proyecto en modo `Release` o `Debug` para generar el archivo ensamblado `ESAPI_CiedSafetyAuditor.dll`.
4. En Eclipse, abrir el menú de Scripts (`Tools -> Scripts`), seleccionar el archivo compilado y ejecutar.

## 📊 Especificaciones Técnicas de Estilo
El código fuente se rige estrictamente bajo las convenciones oficiales de desarrollo de Microsoft .NET:
* `PascalCase` para nombres de clases, métodos y propiedades públicas.
* `camelCase` para variables locales y parámetros.
* `_camelCase` para campos privados de clase.
* Uso exclusivo de `string.Format` para garantizar portabilidad absoluta del binario entre terminales de planificación.

## 📄 Licencia
Este proyecto es de código abierto y se distribuye bajo la Licencia MIT.
