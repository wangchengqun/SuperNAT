const getters = {
  device: state => state.app.device,
  theme: state => state.app.theme,
  color: state => state.app.color,
  user: state => state.user.user,
  dialog: state => state.user.dialog
}

export default getters