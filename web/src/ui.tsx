import { useEffect, useRef, type ReactNode } from 'react';
export const names:Record<string,string> = {codex:'Codex',copilot:'GitHub Copilot',claude:'Claude Code'};
export const sourceName = (id?:string) => names[id??'']??id??'Agent';
export function relative(iso?:string|null) {
  if(!iso)return 'Not yet';const m=Math.max(0,Math.floor((Date.now()-Date.parse(iso))/60000));
  return m<1?'Just now':m<60?m+'m ago':m<1440?Math.floor(m/60)+'h ago':Math.floor(m/1440)+'d ago';
}
export function duration(start?:string|null,end?:string|null) {
  if(!start)return '—';const n=Math.max(0,Math.floor(((end?Date.parse(end):Date.now())-Date.parse(start))/1000));
  return (Math.floor(n/3600)?Math.floor(n/3600)+'h ':'')+Math.floor(n%3600/60)+'m '+String(n%60).padStart(2,'0')+'s';
}
export function Icon({kind}:{kind:string}) {
  const paths:Record<string,ReactNode>={
    plus:<path d="M12 5v14M5 12h14"/>,close:<path d="m6 6 12 12M6 18 18 6"/>,chevron:<path d="m6 9 6 6 6-6"/>,check:<path d="m5 12 4 4L19 6"/>,
    grid:<><rect x="4" y="4" width="6" height="6"/><rect x="14" y="4" width="6" height="6"/><rect x="4" y="14" width="6" height="6"/><rect x="14" y="14" width="6" height="6"/></>,
    inbox:<path d="m5 4-3 9v7h20v-7l-3-9ZM2 13h6l2 3h4l2-3h6"/>,brand:<path d="m4 19 8-14 8 14M8 14h8"/>,clock:<><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></>,
    codex:<path d="m9 7-5 5 5 5m6-10 5 5-5 5M13 4l-2 16"/>,claude:<path d="M12 2v20M2 12h20M5 5l14 14M5 19 19 5M8 3l8 18M3 8l18 8M3 16l18-8M8 21l8-18"/>,
    copilot:<><rect x="3" y="6" width="18" height="13" rx="5"/><path d="M9 6V3h6v3M8 11v2m8-2v2M9 16h6"/></>,sliders:<path d="M4 7h16M4 17h16M8 4v6m8 4v6"/>,
    shield:<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6Zm-4 9 3 3 5-6"/>,activity:<path d="M2 12h4l3-8 6 16 3-8h4"/>,folder:<path d="M3 6h7l2 3h9v11H3Z"/>,message:<path d="M3 3h18v14H8l-5 4ZM7 8h10M7 12h6"/>,search:<><circle cx="10" cy="10" r="6"/><path d="m15 15 5 5"/></>
  };
  return <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[kind]??paths.activity}</svg>;
}
export function Modal({title,close,children}:{title:string;close:()=>void;children:ReactNode}) {
  const ref=useRef<HTMLDialogElement>(null);
  useEffect(()=>{const dialog=ref.current!;dialog.showModal();const cancel=()=>close();dialog.addEventListener('cancel',cancel);return()=>{dialog.removeEventListener('cancel',cancel);dialog.close();};},[close]);
  return <dialog ref={ref} aria-label={title}><div className="detail-inner"><div className="dialog-heading"><h2>{title}</h2><button className="button-icon" aria-label="Close" onClick={close}><Icon kind="close"/></button></div>{children}</div></dialog>;
}
export function Empty({title,message}:{title:string;message:string}) {return <div className="empty-state"><Icon kind="inbox"/><h3>{title}</h3><p>{message}</p></div>;}
