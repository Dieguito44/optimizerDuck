# Banco de pruebas de Zentra

Cómo se prueba Zentra antes de usarlo con clientes (plan de mejoras F2-02). Hay cinco niveles. Cada uno cubre lo que el anterior no puede.

| Nivel | Dónde | Qué cubre | Quién |
|---|---|---|---|
| 1. Tests locales | PC de desarrollo | Lógica, reglas, vista previa, lista negra (~1480 tests) | `dotnet test` |
| 2. CI | Runner de GitHub (Windows Server, desechable, elevado) | Aplicar y revertir **de verdad** cada ajuste del perfil y comprobar que todo vuelve a como estaba | Automático en cada PR |
| 3. Windows Sandbox | Dentro del PC de desarrollo | Ver la interfaz en un Windows limpio, sin tocar el PC | Diego |
| 4. Máquina virtual | Hyper-V con punto de control | Lo que necesita reinicio y Smart App Control **activo** (firma) | Diego |
| 5. PCs reales | Matriz de abajo | Hardware real: portátiles, GPU híbrida, SATA, Home, otros idiomas | Diego |

## Reglas

- **Zentra lo abre Diego, no Claude.** Claude Desktop es una app MSIX: todo lo que lanza desde su terminal hereda la virtualización de `AppData` del paquete. Los datos quedan en `%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Local\Zentra` y el Explorador real no los ve ("Ubicación no disponible"). Doble clic en `Zentra.exe`.
- **Punto de restauración antes de aplicar** cualquier cosa en un PC real. Zentra lo exige en el perfil.
- **Primero la vista previa.** "Aplicar perfil" muestra cada valor que cambiaría (ahora → después) antes de confirmar. Cancelar no cambia nada.
- **Aplicar y revertir en la misma prueba**, y comparar con el estado de antes.
- **Nada de la lista negra**, ni siquiera para probar.

## Nivel 1: tests locales

```powershell
dotnet test optimizerDuck.Test/optimizerDuck.Test.csproj -c Release
```

Debe dar 0 fallos. Los omitidos son pruebas que necesitan elevación o un estado del PC que no se da. Entre ellos está el de aplicar y revertir de verdad, que solo corre en el CI.

## Nivel 2: CI

Cada PR corre la suite completa en `windows-latest`. Ahí `ProfileApplyRevertCiTests` aplica cada ajuste del Servicio estándar, comprueba que quedó como pide el perfil, lo revierte y exige que cada valor (registro, servicio, tarea, directiva) vuelva exactamente al de antes. El runner es Windows Server: los ajustes que no aplican a esa edición se saltan por su condición.

## Nivel 3: Windows Sandbox

Sirve para mirar la interfaz en un Windows limpio sin tocar el PC. Se borra entero al cerrarlo.

1. Activar la característica, una sola vez, como administrador. Pide reiniciar:
   ```powershell
   Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
   ```
2. Publicar la versión de prueba:
   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\publicar-prueba.ps1
   ```
3. Doble clic en `tools\zentra-prueba.wsb`. Abre Zentra solo, sin internet, con la carpeta de la versión en solo lectura.

## Nivel 4: máquina virtual (Hyper-V)

Es para lo que el Sandbox no hace: reinicios (modo juego avanzado, puntos de restauración, servicios) y **Smart App Control activo**, para validar la firma de código.

- Windows 11 limpio, recién instalado (Smart App Control solo se activa en instalaciones limpias).
- **Punto de control antes de cada prueba**, y volver a él al terminar.
- Para la firma: instalar el `.exe` firmado y revisar en el Visor de eventos `Microsoft-Windows-CodeIntegrity/Operational` que no haya eventos "Smart App Control Block".

## Nivel 5: PCs reales

| PC | Por qué | Reglas y acciones que valida |
|---|---|---|
| Portátil con GPU híbrida y batería | Solo se probó en una APU de escritorio | R-GPU-HYBRID, R-BATTERY (ciclos), R-THERMAL, R-RAM-XMP (no aplica) |
| Escritorio con SSD SATA | Desgaste SATA (smartctl), TRIM | R-SSD-WEAR, R-SSD-HEALTH |
| Escritorio con NVMe | Desgaste NVMe nativo | R-SSD-WEAR |
| Windows 11 Home | Ajustes que dependen de la edición (directivas) | Noticias e intereses, perfil completo |
| Windows en otro idioma | Salidas de winget, DISM, chkdsk y sfc traducidas | R-APPS-OUTDATED, Reparar Windows |
| Cliente con cuenta estándar | El técnico eleva con otra cuenta | Aviso en Sesión y ajustes en la cuenta del cliente |

## Checklist por PR

- [ ] Tests locales: 0 fallos.
- [ ] CI en verde, incluido el de aplicar y revertir.
- [ ] Diagnóstico completo en pantalla, con Zentra abierto por Diego.
- [ ] Vista previa del perfil y Cancelar: nada cambia.
- [ ] Enlaces, PDF y carpetas se abren sin elevar.
- [ ] Si el PR toca algo que se aplica: aplicar y revertir en el PC de prueba, con punto de restauración.
