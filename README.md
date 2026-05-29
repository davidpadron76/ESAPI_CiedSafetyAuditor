# ESAPI_CiedSafetyAuditor

`ESAPI_CiedSafetyAuditor` es un script binario de automatización (*Binary Plug-in*) desarrollado en C# para la API de Eclipse (ESAPI) de Varian Medical Systems. Su propósito es actuar como una barrera de control proactiva y automatizada para la gestión de riesgos y protección radiológica en pacientes portadores de Dispositivos Electrónicos Cardíacos Implantables (CIEDs), tales como marcapasos y desfibriladores (ICD), siguiendo las recomendaciones internacionales del reporte **AAPM TG-203**.

Este proyecto fue seleccionado para **Presentación Oral** en el *XIII Congreso Regional de Seguridad Radiológica y Nuclear (XI Congreso Regional IRPA 2026)* en Medellín, Colombia.

## 🚀 Características y Arquitectura por Fases

El script está diseñado bajo un enfoque modular y unificado en un único archivo para maximizar la portabilidad y asegurar la retrocompatibilidad con entornos clínicos que utilicen versiones previas de la API (compatibilidad estricta con C# 5.0 y remoción de interpolación de cadenas):

* **Fase 1: Módulo de Contexto y Detección Estructural:** Identificación algorítmica infalible del contorno del dispositivo en el `StructureSet` activo utilizando expresiones regulares (`RegEx`) insensibles a mayúsculas/minúsculas (`CIED`, `Marcapasos`, `ICD`, `Desfibrilador`).
* **Fase 2: Extracción Dosimétrica Vóxel por Vóxel:** Interrogación directa de la matriz tridimensional de dosis de Eclipse para aislar la Dosis Máxima ($D_{max}$) y la dosis en volumen ($D_{5\%}$), evitando el suavizado de histogramas (*binning*) del DVH convencional.
* **Fase 3: Auditoría de Haces y Análisis Geométrico:** Inspección automatizada de las energías del plan para identificar umbrales críticos de contaminación por neutrones fotonucleares ($\ge 10\text{ MV}$) y cálculo de la distancia euclidiana periférica mínima utilizando los límites de la malla estructural (`MeshGeometry.Bounds`).
* **Fase 4: Motor Clínico de Evaluación de Riesgo:** Clasificación algorítmica del nivel de riesgo del paciente (Bajo, Moderado, Alto) cruzando dosis, energía y distancia, desplegando de forma inmediata las barreras de control y recomendaciones médicas sugeridas por el TG-203.

## 🛠️ Requisitos e Instalación

* **Entorno:** Eclipse Treatment Planning System (Varian Medical Systems).
* **Compatibilidad de API:** Verificado en librerías nativas `VMS.TPS.Common.Model.API` y `VMS.TPS.Common.Model.Types`.
* **Compilación:** Microsoft Visual Studio (Class Library .NET Framework).

### Instrucciones:
1. Clonar el repositorio.
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
