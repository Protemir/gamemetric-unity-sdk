# GameMetric — Demo Scene

A one-file, zero-setup demo of the SDK.

## Run it

1. In **Edit → Project Settings → GameMetric**, paste your project **API Key**
   (or leave the demo's inline field to type one at runtime).
2. Create an empty scene, add an empty **GameObject**, and attach
   **`GameMetricDemoUI`** to it.
3. Press **Play**. An on-screen panel appears with four buttons:
   - **Initialize SDK** — starts a session (from Project Settings, or from the
     inline API-Key field if you typed one).
   - **Log Test Event** — queues a `demo_button_click` event.
   - **Log Monetization ($4.99)** — queues an `in_app_purchase` event.
   - **Force Flush** — asks the dispatcher to deliver now.

The panel mirrors the SDK's own `[GameMetric]` console logs, so you can watch
events get **queued → delivered** (or **cached → retried** if the server is
unreachable) in real time.

> The demo forces debug logs on when you initialize with an inline key. When
> initializing from Project Settings, enable **Enable Debug Logs** there to see
> the delivery activity on screen.

No scene asset is shipped on purpose — the UI is drawn with `OnGUI()`, so
attaching the component to any GameObject is all it takes.