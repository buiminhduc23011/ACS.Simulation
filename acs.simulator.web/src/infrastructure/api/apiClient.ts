import axios from 'axios';
import { API_BASE_URL } from '../../config/config';

const apiClient = axios.create({
  baseURL: API_BASE_URL || undefined,
  timeout: 15000,
  headers: { 'Content-Type': 'application/json' },
});

apiClient.interceptors.response.use(
  (res) => res,
  (err) => {
    console.error('[API Error]', err.response?.status, err.config?.url, err.message);
    return Promise.reject(err);
  }
);

export default apiClient;
