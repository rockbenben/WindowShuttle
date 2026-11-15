# WindowShuttle

> Mueve ventanas entre monitores sin arrastrar: una a una o la pantalla completa a la vez

[⬇ Descargar para Windows](https://github.com/rockbenben/WindowShuttle/releases/latest)

[English](../../README.md) · [简体中文](../../README.zh.md) · [繁體中文](README.zh-Hant.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Deutsch](README.de.md) · **Español** · [Français](README.fr.md) · [Italiano](README.it.md) · [Nederlands](README.nl.md) · [Português](README.pt.md) · [Русский](README.ru.md) · [Türkçe](README.tr.md) · [Tiếng Việt](README.vi.md) · [ไทย](README.th.md) · [Bahasa Indonesia](README.id.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../../LICENSE) [![365 Open Source Plan #033](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23033-1f6feb)](https://github.com/rockbenben/365opensource)

![Mapa de monitores conectados según su disposición física](../images/main-en.png)

**Mover una ventana a otro monitor no debería requerir un largo arrastre por todo el escritorio.** Mantén pulsado `Ctrl` y el botón derecho, haz un deslizamiento rápido hacia la pantalla deseada y la ventana bajo el cursor ya estará allí. ¿Necesitas mover toda la pantalla? Un atajo intercambia todas las ventanas entre tu pantalla actual y la principal. WindowShuttle vive en la bandeja del sistema y funciona al instante sin instalación.

- **Desliza, no apuntes.** `Ctrl` + botón derecho, lanza el puntero hacia una pantalla y suelta. Sin complicaciones con números de pantalla. ¿Te equivocaste? `Ctrl` + clic central lo deshace.
- **Una ventana o la pantalla completa.** Siete acciones directas: enviar la ventana activa, intercambiar dos pantallas por completo o reunir todas las ventanas tras desconectar un monitor.
- **Geometría recalculada.** Adaptación automática a pantallas con diferente escala DPI y resolución.
- **El mapa es el panel de control.** Observa tus pantallas con sus proporciones físicas y arrastra ventanas fácilmente entre tarjetas.
- **Sin compromisos, totalmente offline.** Un único ejecutable portátil, sin instalador ni cuentas. Solo para Windows.

## Compatibilidad

| Área | Soporte |
|---|---|
| **Sistema** | Windows 10 u 11 |
| **Arquitectura** | x64 |
| **.NET** | Versión autónoma no requiere nada; la ligera requiere [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| **Monitores** | 2 o más monitores |

## Las siete acciones

| Acción | Atajo de teclado | Gesto del ratón | Descripción |
|---|---|---|---|
| **Intercambiar pantallas** | `Win`+`Shift`+`X` | `Ctrl`+clic derecho hacia la izquierda | Intercambia todas las ventanas entre la pantalla actual y la principal |
| **Intercambiar ventanas frontales** | `Win`+`Shift`+`Z` | `Ctrl`+clic derecho hacia la derecha | Intercambia solo las ventanas en primer plano de ambas pantallas |
| **Enviar al principal** | *(sin asignar)* | *(sin asignar)* | Envía la ventana activa a la pantalla principal |
| **Enviar al siguiente monitor** | *(sin asignar)* | *(sin asignar)* | Mueve la ventana al monitor de la derecha |
| **Enviar por dirección** | *(solo gesto)* | `Ctrl`+deslizamiento derecho | Mueve la ventana bajo el cursor hacia la pantalla señalada |
| **Reunir todo** | *(sin asignar)* | *(sin asignar)* | Reúne todas las ventanas secundarias en la pantalla principal |
| **Deshacer** | *(sin asignar)* | `Ctrl`+clic central | Deshace el movimiento anterior y devuelve las ventanas a su lugar |
| **Número de pantalla** | *(sin asignar)* | *(sin asignar)* | Muestra brevemente el número en el centro de cada pantalla |

## Configuración y CLI

- **Iniciar con Windows**: inicio automático minimizado en la bandeja del sistema.
- **Como administrador**: ejecuta mediante una tarea programada con privilegios elevados para solucionar restricciones UIPI.
- **Rescatar ventanas fuera de pantalla**: recupera ventanas perdidas al desconectar pantallas.
- **Omitir aplicaciones a pantalla completa**: protege juegos o vídeos activos.
- CLI: `WindowShuttle.exe list`, `swap`, `swap-top`, `to-primary`, `gather`, `to-next`, `to <n>`, `identify`, `undo`.

## Sobre el Plan 365 de código abierto

Proyecto **#033** del [Plan 365 de código abierto](https://github.com/rockbenben/365opensource) — una persona + IA, más de 300 proyectos de código abierto en un año.

[Envía tu idea →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
