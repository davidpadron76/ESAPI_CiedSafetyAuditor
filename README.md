# ESAPI_CiedSafetyAuditor

`ESAPI_CiedSafetyAuditor` es un script binario de automatización (*Binary Plug-in*) desarrollado en C# para la API de Eclipse (ESAPI) de Varian Medical Systems. Su propósito es actuar como una barrera de control proactiva y automatizada para la gestión de riesgos y protección radiológica en pacientes portadores de Dispositivos Electrónicos Cardíacos Implantables (CIEDs), tales como marcapasos y desfibriladores (ICD), siguiendo las recomendaciones internacionales del reporte **AAPM TG-203**.

Este proyecto fue seleccionado para **Presentación Oral** en el *XIII Congreso Regional de Seguridad Radiológica y Nuclear (XI Congreso Regional IRPA 2026)* en Medellín, Colombia.

## 🚀 Características y Arquitectura por Fases

El script está diseñado bajo un enfoque modular y unificado en un único archivo para maximizar la portabilidad y asegurar la retrocompatibilidad con entornos clínicos que utilicen versiones previas de la API (compatibilidad estricta con C# 5.0 y remoción de interpolación de cadenas):

* **Fase 1: Módulo de Contexto y Detección Estructural:** Identificación algorítmica del contorno del dispositivo en el `StructureSet` activo utilizando expresiones regulares (`RegEx`) insensibles a mayúsculas/minúsculas (`CIED`, `Marcapasos`, `ICD`, `Desfibrilador`). Si el patrón coincide con varias estructuras (por ejemplo `CIED` y `CIED_PRV` en el mismo plan), se selecciona automáticamente la de menor volumen y se advierte al usuario sobre las candidatas detectadas, evitando depender del orden no garantizado de `StructureSet.Structures`.
* **Fase 2: Extracción Dosimétrica:** Interrogación directa de la distribución de dosis de Eclipse para aislar la Dosis Máxima (**Dmax**, vía `DVHData.MaxDose`, independiente de la resolución de bin) y la dosis en volumen (**D5%**, vía `GetDoseAtVolume`), sin depender de la interpolación de la curva DVH acumulada.
* **Fase 3: Auditoría de Haces y Análisis Geométrico *Beam's-Eye-View*:** Inspección automatizada de las energías del plan para identificar umbrales críticos de contaminación por neutrones fotonucleares (mayor o igual a **10 MV**) y cálculo de la distancia mínima real entre el dispositivo y el borde del campo. Cada vértice de la malla del CIED se transforma al sistema de coordenadas del haz según **IEC 61217**, aplicando la orientación del paciente, la rotación de gantry, colimador y camilla, y corrigiendo por divergencia del haz respecto al plano del isocentro. La apertura se evalúa como la **conformada por el MLC** (unión de los huecos de cada par de láminas, recortada por las mordazas), no como el rectángulo de mordazas: en VMAT las mordazas suelen permanecer abiertas durante todo el arco y es el MLC quien define el campo real, por lo que una evaluación por mordazas resultaría casi siempre en distancia cero. El recorrido cubre **todos los control points** de cada haz, de modo que en técnicas de arco se detecta cualquier ángulo en que el dispositivo entre al campo — algo que una evaluación del primer control point pasaba por alto. El reporte informa el haz y los ángulos de gantry, colimador y camilla donde se alcanza el mínimo.
* **Fase 4: Motor Clínico de Evaluación de Riesgo:** Clasificación algorítmica del nivel de riesgo del paciente (Bajo, Moderado, Alto) cruzando dosis, energía y distancia, desplegando de forma inmediata las barreras de control y recomendaciones médicas sugeridas por el TG-203.
* **Reporte Visual con Semáforo por Ítem:** El resultado final se presenta en una ventana dedicada (no un simple `MessageBox`) con un indicador de color (verde/ámbar/rojo) y el umbral de referencia junto a cada medición (Dmax, energía/neutrones, distancia al borde), además del semáforo y veredicto global — así un usuario sin formación en dosimetría puede ver de un vistazo qué tan cerca o lejos está cada parámetro de cambiar de categoría de riesgo, no solo el resultado final.

## ⚠️ Validación Pendiente del Cálculo Geométrico

> **El cálculo *beam's-eye-view* de la Fase 3 no ha sido validado experimentalmente y no debe utilizarse como base de decisiones clínicas hasta que lo esté.**

La transformación IEC 61217 implica convenciones de signo de rotación que dependen del fabricante y de la escala configurada en el acelerador. Las asumidas en el código están documentadas explícitamente en los comentarios de `BeamEyeViewProjector`, pero requieren verificación contra casos conocidos:

1. **Campo único, gantry 0, CIED lateral al campo:** la distancia calculada debe coincidir con la medida manualmente sobre el corte axial en Eclipse.
2. **Campo único, gantry 90 y 270:** verifica que el signo de la rotación de gantry sea correcto — un signo invertido intercambia los resultados de estos dos ángulos.
3. **Campo rectangular asimétrico con colimador ≠ 0:** verifica la convención de rotación de colimador.
4. **Plan con camilla ≠ 0:** verifica la convención de rotación de camilla, la menos ejercitada del conjunto ya que la mayoría de los planes usan camilla 0.
5. **CIED deliberadamente dentro del campo:** debe reportar exactamente 0.0 cm.

6. **Orden de las láminas del MLC:** con una apertura deliberadamente asimétrica en Y, confirma que el índice 0 de `LeafPositions` corresponde al extremo Y1 (negativo) y no al opuesto.

## ⚠️ Limitaciones Conocidas

* Las fronteras en Y de las láminas no están expuestas por ESAPI y se derivan del identificador del MLC contra una tabla de modelos Varian conocidos (Millennium 120, HD120, Millennium 80). **Si el modelo no se reconoce, el MLC no se evalúa** y el cálculo cae a mordazas: el reporte lo indica explícitamente en la línea "Apertura evaluada".
* La detección de neutrones por fotoactivación (≥10 MV) no distingue entre modalidad de fotones y electrones; se basa únicamente en el valor numérico de energía del haz.
* Sólo se admiten las orientaciones de tratamiento HFS, HFP, FFS y FFP. Las orientaciones en decúbito lateral se rechazan explícitamente en lugar de calcularse de forma incorrecta.
* La distancia fuente-eje se asume en el valor nominal de **1000 mm**.

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
