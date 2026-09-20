import type { Device } from './api';

export function heartbeatAge(value?: string|null) {
  if (!value) return 'Not received yet';
  const seconds=Math.max(0,Math.floor((Date.now()-Date.parse(value))/1000));
  if (!Number.isFinite(seconds)) return 'Unknown';
  return seconds<60?seconds+'s ago':seconds<3600?Math.floor(seconds/60)+'m ago':seconds<86400?Math.floor(seconds/3600)+'h ago':Math.floor(seconds/86400)+'d ago';
}

export function DeviceCard({device,connected,busy,disconnect}:{device:Device;connected:boolean;busy:boolean;disconnect:()=>void}) {
  const status=device.local?'manual':!connected?'unknown':device.status;
  const label=status==='online'?'Online':status==='offline'?'Offline':status==='pending'?'Waiting for heartbeat':status==='manual'?'Manual tracking':'Connection unknown';
  return <article className="device-card" data-status={status} aria-label={device.name+': '+label}>
    <div className="device-card-heading"><h3>{device.name}</h3><span className="device-status"><span className="device-status-dot" aria-hidden="true"/>{label}</span></div>
    <p className="device-task-count">{device.runningCount} running tasks</p>
    {device.local?<p>Task leases are tracked here; no device heartbeat is required.</p>:<>
      <p className="device-heartbeat">Last heartbeat <time dateTime={device.lastSeenAt??undefined} title={device.lastSeenAt?new Date(device.lastSeenAt).toLocaleString():undefined}>{heartbeatAge(device.lastSeenAt)}</time></p>
      <p className="device-status-detail">{!connected?'Reconnect to the dashboard to refresh device status.':device.status==='pending'?'Start the collector on this device to connect.':device.status==='offline'?'No heartbeat received. Check that its collector is running.':'Heartbeat every '+(device.heartbeatSeconds??10)+'s · offline after '+(device.offlineAfterSeconds??45)+'s'}</p>
      {connected&&device.status==='online'&&device.taskDataStale&&<p className="device-warning">{device.lastReportAt?'Task updates delayed — old tasks do not count as running.':'Waiting for the first task update.'}</p>}
      <button className="button button-quiet" disabled={busy} onClick={disconnect}>Disconnect</button>
    </>}
  </article>;
}
