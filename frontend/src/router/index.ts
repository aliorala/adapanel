import { createRouter, createWebHistory } from "vue-router";
import SetupWizardView from "../views/SetupWizardView.vue";

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: "/:pathMatch(.*)*",
      name: "panel",
      component: SetupWizardView
    }
  ]
});

export default router;
