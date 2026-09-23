import React, { useEffect, useRef, useCallback } from 'react';
import nipplejs from 'nipplejs';
import type { JoystickManager, JoystickOutputData } from 'nipplejs';

interface Props {
  onMove: (throttle: number, steering: number) => void;
  onEnd: () => void;
  disabled?: boolean;
}

export const VirtualJoystick: React.FC<Props> = ({ onMove, onEnd, disabled }) => {
  const containerRef = useRef<HTMLDivElement>(null);
  const managerRef = useRef<JoystickManager | null>(null);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const directionRef = useRef({ throttle: 0, steering: 0 });

  const stopInterval = useCallback(() => {
    if (intervalRef.current) {
      clearInterval(intervalRef.current);
      intervalRef.current = null;
    }
    directionRef.current = { throttle: 0, steering: 0 };
  }, []);

  useEffect(() => {
    if (!containerRef.current || disabled) return;

    const manager = nipplejs.create({
      zone: containerRef.current,
      mode: 'static',
      position: { left: '50%', top: '50%' },
      color: '#58a6ff',
      size: 120,
    });

    managerRef.current = manager;

    manager.on('move', (_: unknown, data: JoystickOutputData) => {
      const angle = data.angle?.radian ?? 0;
      const force = Math.min(data.force ?? 0, 2) / 2; // normalize 0..1
      directionRef.current = {
        throttle: Math.sin(angle) * force,
        steering: Math.cos(angle) * force,
      };

      if (!intervalRef.current) {
        // Fire immediately on first touch for instant response
        onMove(directionRef.current.throttle, directionRef.current.steering);
        intervalRef.current = setInterval(() => {
          onMove(directionRef.current.throttle, directionRef.current.steering);
        }, 100);
      }
    });

    manager.on('end', () => {
      stopInterval();
      onEnd();
    });

    return () => {
      stopInterval();
      manager.destroy();
      managerRef.current = null;
    };
  }, [disabled, onMove, onEnd, stopInterval]);

  return (
    <div style={{ position: 'relative', width: 220, height: 220 }}>
      <div
        ref={containerRef}
        style={{
          width: 200,
          height: 200,
          position: 'absolute',
          inset: '10px',
          borderRadius: '50%',
          background: 'rgba(88, 166, 255, 0.08)',
          border: '2px dashed rgba(88, 166, 255, 0.3)',
          opacity: disabled ? 0.4 : 1,
          pointerEvents: disabled ? 'none' : 'auto',
        }}
      />
      <div style={{ position: 'absolute', top: 0, left: '50%', transform: 'translateX(-50%)', fontSize: 12, color: '#8fb8ff' }}>
        Forward
      </div>
      <div style={{ position: 'absolute', bottom: 0, left: '50%', transform: 'translateX(-50%)', fontSize: 12, color: '#ffb86b' }}>
        Reverse
      </div>
      <div style={{ position: 'absolute', left: 0, top: '50%', transform: 'translateY(-50%)', fontSize: 12, color: '#9fdc9f' }}>
        Turn Left
      </div>
      <div style={{ position: 'absolute', right: 0, top: '50%', transform: 'translateY(-50%)', fontSize: 12, color: '#9fdc9f' }}>
        Turn Right
      </div>
    </div>
  );
};
