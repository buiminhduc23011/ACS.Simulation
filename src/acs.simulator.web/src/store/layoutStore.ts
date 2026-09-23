import { create } from 'zustand';
import React from 'react';

interface LayoutState {
  headerLeft: React.ReactNode | null;
  setHeaderLeft: (node: React.ReactNode | null) => void;
}

export const useLayoutStore = create<LayoutState>((set) => ({
  headerLeft: null,
  setHeaderLeft: (node) => set({ headerLeft: node }),
}));
