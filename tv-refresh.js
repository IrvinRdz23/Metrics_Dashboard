/* tv-refresh.js - utilidades para TVs de piso (Furnace / General / Línea-Celda)
 *
 * 1) PM.scheduleRefresh()  -> recarga la página a las 00:00, 06:00, 12:00 y 18:00 (hora local del navegador).
 * 2) PM.startHub(opts)     -> conexión SignalR que NO se rinde: reintenta para siempre, y vuelve a unirse
 *                             al grupo (JoinFurnace / JoinLine) después de cada reconexión.
 *                             Si queda desconectada más de 90 s, recarga la página como último recurso.
 */
window.PM = (function () {
    const REFRESH_HOURS = [0, 6, 12, 18];

    function nextRefreshAt(now) {
        const d = new Date(now);
        for (const h of REFRESH_HOURS) {
            const t = new Date(d.getFullYear(), d.getMonth(), d.getDate(), h, 0, 0, 0);
            if (t.getTime() > now) return t.getTime();
        }
        return new Date(d.getFullYear(), d.getMonth(), d.getDate() + 1, 0, 0, 0, 0).getTime();
    }

    function scheduleRefresh() {
        // Pequeño desfase aleatorio (0-20 s) para que todas las TVs no golpeen el servidor al mismo segundo.
        const jitter = Math.floor(Math.random() * 20000);
        let target = nextRefreshAt(Date.now()) + jitter;
        // setInterval en vez de un setTimeout largo: si la PC se duerme o el reloj se ajusta, se corrige solo.
        setInterval(() => {
            if (Date.now() >= target) {
                // 'location.reload()' recarga la misma URL (conserva ?date=&shiftId= en histórico).
                location.reload();
            }
        }, 15000);
    }

    /**
     * opts = {
     *   url: '/hubs/metrics',
     *   handlers: { NombreEvento: fn, ... },
     *   onConnected: (connection) => connection.invoke('JoinFurnace', id)   // se ejecuta al conectar Y al reconectar
     * }
     */
    function startHub(opts) {
        // Reintentos infinitos: 0s, 2s, 5s, 10s y luego cada 15 s. (El default de SignalR se rinde tras 4 intentos.)
        const retryPolicy = {
            nextRetryDelayInMilliseconds: (ctx) => [0, 2000, 5000, 10000][ctx.previousRetryCount] ?? 15000
        };
        const connection = new signalR.HubConnectionBuilder()
            .withUrl(opts.url)
            .withAutomaticReconnect(retryPolicy)
            .build();

        Object.entries(opts.handlers || {}).forEach(([name, fn]) => connection.on(name, fn));

        let downSince = null;
        const joined = async () => {
            downSince = null;
            try { if (opts.onConnected) await opts.onConnected(connection); }
            catch (e) { console.error('SignalR join error:', e); }
        };

        async function start() {
            try { await connection.start(); await joined(); }
            catch (e) { console.error('SignalR start error:', e); setTimeout(start, 5000); }
        }

        // IMPORTANTE: tras una reconexión automática el servidor ya NO te tiene en el grupo; hay que volver a unirse.
        connection.onreconnecting(() => { downSince = downSince || Date.now(); });
        connection.onreconnected(joined);
        // Si SignalR se rinde (Closed), se vuelve a arrancar a mano.
        connection.onclose(() => { downSince = downSince || Date.now(); setTimeout(start, 5000); });

        // Perro guardián: más de 90 s sin conexión => recarga completa.
        setInterval(() => {
            if (downSince && Date.now() - downSince > 90000) location.reload();
        }, 10000);

        start();
        return connection;
    }

    return { scheduleRefresh, startHub };
})();
