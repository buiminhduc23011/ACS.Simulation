import { useEffect, useRef, useCallback } from 'react';
import * as signalR from '@microsoft/signalr';
import { SIGNALR_URL } from '../../config/config';
import type { ISimAgv } from '../../types/agv';
import type { IFleetEvent, ILogMessage } from '../../types/events';
import type { IScenarioRun, IStepResult } from '../../types/scenario';

export interface SimulatorSignalRCallbacks {
  onFleetUpdated?: (agvs: ISimAgv[]) => void;
  onFleetEvent?: (event: IFleetEvent) => void;
  onScenarioStarted?: (run: IScenarioRun) => void;
  onScenarioStep?: (result: IStepResult) => void;
  onScenarioProgress?: (run: IScenarioRun) => void;
  onScenarioFinished?: (run: IScenarioRun) => void;
  onLogMessage?: (log: ILogMessage) => void;
}

export function useSimulatorSignalR(
  groups: string[],
  callbacks: SimulatorSignalRCallbacks
) {
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const callbacksRef = useRef(callbacks);
  callbacksRef.current = callbacks;

  const joinGroups = useCallback(async (conn: signalR.HubConnection) => {
    for (const group of groups) {
      await conn.invoke('JoinGroup', group).catch(console.error);
    }
  }, [groups]);

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(SIGNALR_URL)
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    connection.on('AgvFleetUpdated', (data: { agvs: ISimAgv[] }) => {
      callbacksRef.current.onFleetUpdated?.(data.agvs ?? data as unknown as ISimAgv[]);
    });

    connection.on('FleetEvent', (event: IFleetEvent) => {
      callbacksRef.current.onFleetEvent?.(event);
    });

    connection.on('ScenarioStarted', (run: IScenarioRun) => {
      callbacksRef.current.onScenarioStarted?.(run);
    });

    connection.on('ScenarioStep', (result: IStepResult) => {
      callbacksRef.current.onScenarioStep?.(result);
    });

    connection.on('ScenarioProgress', (run: IScenarioRun) => {
      callbacksRef.current.onScenarioProgress?.(run);
    });

    connection.on('ScenarioFinished', (run: IScenarioRun) => {
      callbacksRef.current.onScenarioFinished?.(run);
    });

    connection.on('LogMessage', (log: ILogMessage) => {
      callbacksRef.current.onLogMessage?.(log);
    });

    connection.onreconnected(() => joinGroups(connection));

    connection.start()
      .then(() => joinGroups(connection))
      .catch((err) => console.error('[SignalR] Connection failed:', err));

    connectionRef.current = connection;

    return () => {
      connection.stop();
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return connectionRef;
}
